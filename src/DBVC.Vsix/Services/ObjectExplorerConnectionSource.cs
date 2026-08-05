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

        private const string NoUserNameReason =
            "SSMS 연결에서 계정 정보를 읽지 못했습니다. 인증 방식과 계정을 직접 지정하세요.";

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
                SsmsDiagnostics.Trace($"자동 채움 중단: 예외 {ex.GetType().Name} — {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 멈춘 지점을 남기고 <c>null</c>을 돌려준다.
        ///
        /// 이 경로는 SSMS 프로세스 안에서만 실행되므로 단위 테스트가 닿지 않는다. 사유를 남기지
        /// 않으면 "아무 일도 일어나지 않는다"만 남고, 어느 관문에서 막혔는지 알 방법이 없다.
        /// </summary>
        private static SsmsConnectionInfo? Fail(string reason)
        {
            SsmsDiagnostics.Trace($"자동 채움 중단: {reason}");
            return null;
        }

        private static SsmsConnectionInfo? Read()
        {
            var serviceCacheType = FindType(VsIntegrationAssembly, ServiceCacheTypeName);
            var explorerServiceType = FindType(InterfacesAssembly, ObjectExplorerServiceTypeName);
            var nodeContextType = FindType(InterfacesAssembly, NodeContextTypeName);
            if (serviceCacheType == null || explorerServiceType == null || nodeContextType == null)
            {
                // SSMS 셸 밖이거나, 해당 어셈블리가 아직 이 AppDomain에 로드되지 않았다.
                return Fail(
                    $"SSMS 셸 타입을 찾지 못했습니다 (ServiceCache={serviceCacheType != null}, " +
                    $"IObjectExplorerService={explorerServiceType != null}, INodeContext={nodeContextType != null}). " +
                    $"로드된 어셈블리 수={AppDomain.CurrentDomain.GetAssemblies().Length}");
            }

            var provider = serviceCacheType
                .GetProperty("ServiceProvider", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IServiceProvider;
            if (provider == null)
            {
                return Fail("ServiceCache.ServiceProvider가 null이거나 IServiceProvider가 아닙니다.");
            }

            var explorer = provider.GetService(explorerServiceType);
            if (explorer == null)
            {
                return Fail("ServiceProvider가 IObjectExplorerService를 돌려주지 않았습니다.");
            }

            // 인터페이스 타입에서 메서드를 찾는다(위에서 이미 확보한 explorerServiceType). explorer.GetType()으로 찾으면
            // 셸 구현이 명시적 인터페이스 구현이면 GetMethod가 null을 돌려주고, 구현 타입에
            // 오버로드가 있으면 AmbiguousMatchException을 던진다 — 둘 다 조용히 실패로 삼켜져
            // 기능이 원인 없이 죽는다. 인터페이스는 시그니처가 하나뿐이고, 인터페이스의
            // MethodInfo로 Invoke해도 실제 구현으로 가상 디스패치된다.
            var getSelectedNodes = explorerServiceType.GetMethod("GetSelectedNodes");
            if (getSelectedNodes == null)
            {
                return Fail("IObjectExplorerService에 GetSelectedNodes가 없습니다.");
            }

            // void GetSelectedNodes(out int count, out INodeInformation[] nodes)
            var args = new object?[] { 0, null };
            getSelectedNodes.Invoke(explorer, args);

            int count = args[0] is int selected ? selected : 0;
            // 다중 선택은 어느 것을 뜻하는지 정할 근거가 없다. 아무것도 하지 않는다.
            if (count != 1 || !(args[1] is Array nodes) || nodes.Length < 1)
            {
                return Fail(
                    $"선택된 노드가 하나가 아닙니다 (count={count}, " +
                    $"array={(args[1] == null ? "null" : args[1]!.GetType().Name)}).");
            }

            var node = nodes.GetValue(0);
            if (node == null || !nodeContextType.IsInstanceOfType(node))
            {
                return Fail(
                    $"선택 노드가 INodeContext가 아닙니다 (실제 타입={node?.GetType().FullName ?? "null"}).");
            }

            var urn = nodeContextType.GetProperty("Context")?.GetValue(node) as string;
            var databaseName = SsmsUrn.TryGetDatabaseName(urn);
            if (string.IsNullOrEmpty(databaseName))
            {
                return Fail($"URN에서 데이터베이스를 얻지 못했습니다 (Context='{urn}').");
            }

            var connection = nodeContextType.GetProperty("Connection")?.GetValue(node);
            if (connection == null)
            {
                return Fail("노드의 Connection이 null입니다.");
            }

            var serverName = ReadString(connection, "ServerName");
            if (string.IsNullOrEmpty(serverName))
            {
                return Fail($"연결의 ServerName이 비어 있습니다 (연결 타입={connection.GetType().FullName}).");
            }

            // 판정 순서가 중요하다. 측정된 SSMS 21(SqlConnectionInfo, Microsoft.SqlServer.ConnectionInfo
            // 17.100) 동작: UseIntegratedSecurity는 새 인스턴스에서 기본값이 true이고, UserName을
            // 설정하는 부수 효과로만 false가 된다. Authentication을 Entra 계열 값으로 설정해도
            // UseIntegratedSecurity는 그대로 true로 남는다 — ActiveDirectoryIntegrated만 예외적으로
            // false다. 그래서 UseIntegratedSecurity를 먼저 물으면 Device Code·Managed Identity·
            // Default·사용자 이름을 비워 둔 Interactive 같은 Entra 연결이 전부 "Windows 인증,
            // 재사용 가능"으로 오판된다. AccessToken(토큰 기반 연결의 확정적 표지)과 Authentication
            // 문자열로 Entra 여부를 먼저 걸러낸 다음에야 UseIntegratedSecurity를 믿을 수 있다.

            // AccessToken은 파생 타입(SqlConnectionInfo)에만 있다. 값이 있으면 토큰 기반 연결이며,
            // 사용자 이름/암호로 환원할 수 없다 — 다른 속성이 무엇을 말하든 무조건 재사용 불가다.
            if (ReadObject(connection, "AccessToken") != null)
            {
                return new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, EntraReason);
            }

            // Authentication도 파생 타입에만 있다. 없으면 이 단계에서는 Entra가 아니라는 뜻이다.
            var authentication = connection.GetType().GetProperty("Authentication")
                ?.GetValue(connection)?.ToString();
            if (authentication != null && authentication.StartsWith("ActiveDirectory", StringComparison.Ordinal))
            {
                return new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, EntraReason);
            }

            // 여기까지 왔다면 두 Entra 표지가 모두 없었다는 뜻이므로, 이제야 UseIntegratedSecurity를
            // 믿고 진짜 Windows 통합 인증으로 판정할 수 있다.
            if (ReadBool(connection, "UseIntegratedSecurity"))
            {
                return new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, null);
            }

            var userName = ReadString(connection, "UserName");
            if (string.IsNullOrEmpty(userName))
            {
                // 로그인 계정이 없는 SQL 인증 주장은 저장소가 방금 복원해 둔 사용자 이름을
                // null로 덮어써 지운다. 자동 채움을 포기하는 편이 낫다.
                return new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Sql, null, null, NoUserNameReason);
            }

            var password = ReadPassword(connection);
            SsmsDiagnostics.Trace(
                $"자동 채움: {serverName}.{databaseName} SQL 인증, 계정={userName}, 암호 확보={password != null}");

            return new SsmsConnectionInfo(
                serverName!,
                databaseName!,
                SqlAuthMode.Sql,
                userName,
                password,
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

        /// <summary>기반 타입에는 없는 속성일 수도 있다 — 그 경우 자연스럽게 <c>null</c>이다.</summary>
        private static object? ReadObject(object instance, string propertyName)
            => instance.GetType().GetProperty(propertyName)?.GetValue(instance);

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
