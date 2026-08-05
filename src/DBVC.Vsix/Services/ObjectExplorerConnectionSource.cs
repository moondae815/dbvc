using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using DBVC.Core.Models;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// SSMS 개체 탐색기에서 선택된 노드의 연결을 읽는다.
    ///
    /// SSMS 어셈블리를 컴파일 타임에 참조하지 않는다. 그 어셈블리들은 SSMS 설치 폴더에만 있고
    /// GAC에 없으므로, 참조하면 (a) 빌드가 특정 SSMS 설치에 묶이고 — 이 저장소는 비Windows에서도
    /// 컴파일과 단위 테스트가 돌아간다 — (b) 어셈블리 버전이 고정되어 다음 SSMS에서 로드가 깨진다.
    /// 리플렉션은 두 문제를 모두 피하고 실패를 "자동 채움이 안 됨"으로 국한한다.
    ///
    /// 판단 로직을 여기에 두지 않는다. SSMS 밖에서 테스트할 수 없기 때문이다 —
    /// URN 파싱은 <see cref="SsmsUrn"/>로, 나머지는 속성 읽기와 얇은 분기로 유지한다.
    ///
    /// <b>UI 스레드에서만 호출한다.</b> <c>GetSelectedNodes</c>가 개체 탐색기 트리를 건드린다.
    /// 호출 지점(도구 창 가시성 이벤트, 갱신 명령)은 모두 이미 UI 스레드다.
    /// </summary>
    public sealed class ObjectExplorerConnectionSource : ISsmsConnectionSource
    {
        private const string VsIntegrationAssembly = "Microsoft.SqlServer.SqlTools.VSIntegration";
        private const string InterfacesAssembly = "SqlWorkbench.Interfaces";

        private const string ServiceCacheTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache";
        private const string ObjectExplorerServiceTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService";
        private const string NodeContextTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.INodeContext";

        private const string EntraReason =
            "SSMS가 Microsoft Entra ID로 접속해 있습니다. DBVC는 토큰 기반 연결을 재사용할 수 없으니 " +
            "인증 방식과 계정을 직접 지정하세요.";

        public SsmsConnectionInfo? TryGetCurrent()
        {
            try
            {
                return Read();
            }
            catch (Exception ex)
            {
                // 어느 단계가 깨지든 결과는 "자동 채움 없음"이다. 도구 창은 계속 동작해야 한다.
                Debug.WriteLine($"ObjectExplorerConnectionSource.TryGetCurrent failed: {ex.Message}");
                return null;
            }
        }

        private static SsmsConnectionInfo? Read()
        {
            var serviceCacheType = FindType(VsIntegrationAssembly, ServiceCacheTypeName);
            var explorerServiceType = FindType(InterfacesAssembly, ObjectExplorerServiceTypeName);
            var nodeContextType = FindType(InterfacesAssembly, NodeContextTypeName);
            if (serviceCacheType == null || explorerServiceType == null || nodeContextType == null)
            {
                return null;   // SSMS 셸 밖이다.
            }

            var provider = serviceCacheType
                .GetProperty("ServiceProvider", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IServiceProvider;
            var explorer = provider?.GetService(explorerServiceType);
            if (explorer == null) return null;

            var getSelectedNodes = explorer.GetType().GetMethod("GetSelectedNodes");
            if (getSelectedNodes == null) return null;

            // void GetSelectedNodes(out int count, out INodeInformation[] nodes)
            var args = new object?[] { 0, null };
            getSelectedNodes.Invoke(explorer, args);

            int count = args[0] is int selected ? selected : 0;
            // 다중 선택은 어느 것을 뜻하는지 정할 근거가 없다. 아무것도 하지 않는다.
            if (count != 1 || !(args[1] is Array nodes) || nodes.Length < 1) return null;

            var node = nodes.GetValue(0);
            if (node == null || !nodeContextType.IsInstanceOfType(node)) return null;

            var urn = nodeContextType.GetProperty("Context")?.GetValue(node) as string;
            var databaseName = SsmsUrn.TryGetDatabaseName(urn);
            if (string.IsNullOrEmpty(databaseName)) return null;

            var connection = nodeContextType.GetProperty("Connection")?.GetValue(node);
            if (connection == null) return null;

            var serverName = ReadString(connection, "ServerName");
            if (string.IsNullOrEmpty(serverName)) return null;

            if (ReadBool(connection, "UseIntegratedSecurity"))
            {
                return new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, null);
            }

            // Authentication은 파생 타입(SqlConnectionInfo)에만 있다. 없으면 SQL 인증으로 본다.
            var authentication = connection.GetType().GetProperty("Authentication")
                ?.GetValue(connection)?.ToString();
            if (authentication != null && authentication.StartsWith("ActiveDirectory", StringComparison.Ordinal))
            {
                return new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, EntraReason);
            }

            return new SsmsConnectionInfo(
                serverName!,
                databaseName!,
                SqlAuthMode.Sql,
                ReadString(connection, "UserName"),
                ReadPassword(connection),
                null);
        }

        /// <summary>
        /// 로드된 어셈블리에서 타입을 찾는다. SSMS 프로세스 안에서는 이미 로드되어 있으므로
        /// 파일을 직접 로드하지 않는다 — 설치 경로를 추측하지 않아도 되고, 셸 밖에서는
        /// 자연스럽게 <c>null</c>이 된다.
        /// </summary>
        private static Type? FindType(string assemblySimpleName, string typeName)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(
                    a.GetName().Name, assemblySimpleName, StringComparison.OrdinalIgnoreCase));
            return assembly?.GetType(typeName, throwOnError: false);
        }

        private static string? ReadString(object instance, string propertyName)
            => instance.GetType().GetProperty(propertyName)?.GetValue(instance) as string;

        private static bool ReadBool(object instance, string propertyName)
            => instance.GetType().GetProperty(propertyName)?.GetValue(instance) is bool value && value;

        /// <summary>
        /// 평문 <c>Password</c>가 비어 있으면 <c>SecurePassword</c>에서 되돌린다.
        /// SSMS가 암호를 들고 있지 않을 수도 있으므로 <c>null</c>은 정상 결과다.
        /// </summary>
        private static string? ReadPassword(object connection)
        {
            var password = ReadString(connection, "Password");
            if (!string.IsNullOrEmpty(password)) return password;

            if (!(connection.GetType().GetProperty("SecurePassword")?.GetValue(connection) is SecureString secure)
                || secure.Length == 0)
            {
                return null;
            }

            IntPtr pointer = IntPtr.Zero;
            try
            {
                pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(pointer);
                }
            }
        }
    }
}
