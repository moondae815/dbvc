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
    /// 리플렉션은 두 문제를 모두 피한다. 다만 실패를 국한하지는 못한다 — 입력란과 저장된
    /// 자격증명이 없는 지금은 이 읽기가 유일한 연결 경로이므로, 리플렉션이 실패하면
    /// 일부 기능이 아니라 플러그인 전체가 접속할 수단을 잃는다.
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

        // 이 두 문구 안에서 "연결"은 버튼 이름으로만 쓰고, 접속 자체는 "접속"으로 부른다.
        // 둘 다 "연결"이면 "만든 연결을 선택한 뒤 연결을 다시 누르세요"처럼 같은 단어가
        // 연달아 다른 뜻이 되어, 무엇을 고르고 무엇을 누르라는 것인지 흐려진다.
        private const string EntraReason =
            "SSMS가 Microsoft Entra ID로 접속해 있습니다. DBVC는 토큰 기반 접속을 재사용할 수 없습니다. " +
            "개체 탐색기에서 SQL 인증이나 Windows 인증으로 만든 접속을 선택한 뒤 연결을 다시 누르세요.";

        private const string NoUserNameReason =
            "SSMS 접속에서 계정 정보를 읽지 못했습니다. 개체 탐색기에서 해당 서버에 다시 접속한 뒤 " +
            "연결을 다시 누르세요.";

        public SsmsConnectionInfo? TryGetCurrent()
        {
            try
            {
                return Read();
            }
            catch (Exception ex)
            {
                // 어느 단계가 깨지든 이 어댑터는 null을 돌려주고 예외를 삼킨다. 도구 창은
                // 계속 동작해야 하지만, 유일한 연결 경로가 막혔다는 뜻이므로 사용자는
                // Connect를 눌러도 접속할 수 없다.
                Debug.WriteLine($"ObjectExplorerConnectionSource.TryGetCurrent failed: {ex.Message}");
                SsmsDiagnostics.Trace($"개체 탐색기 연결 읽기 중단: 예외 {ex.GetType().Name} — {ex.Message}");
                return null;
            }
        }

        public string? TryGetSelectedUrn()
        {
            try
            {
                var serviceCacheType = FindType(VsIntegrationAssembly, ServiceCacheTypeName);
                var explorerServiceType = FindType(InterfacesAssembly, ObjectExplorerServiceTypeName);
                var nodeContextType = FindType(InterfacesAssembly, NodeContextTypeName);
                if (serviceCacheType == null || explorerServiceType == null || nodeContextType == null)
                {
                    return null;
                }

                var explorer = TryGetObjectExplorerService(explorerServiceType, serviceCacheType);
                if (explorer == null)
                {
                    return null;
                }

                var getSelectedNodes = explorerServiceType.GetMethod("GetSelectedNodes");
                if (getSelectedNodes == null)
                {
                    return null;
                }

                var args = new object?[] { 0, null };
                getSelectedNodes.Invoke(explorer, args);

                int count = args[0] is int selected ? selected : 0;
                if (count != 1 || !(args[1] is Array nodes) || nodes.Length < 1)
                {
                    return null;
                }

                var node = nodes.GetValue(0);
                if (node == null || !nodeContextType.IsInstanceOfType(node))
                {
                    return null;
                }

                return nodeContextType.GetProperty("Context")?.GetValue(node) as string;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ObjectExplorerConnectionSource.TryGetSelectedUrn failed: {ex.Message}");
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
            SsmsDiagnostics.Trace($"개체 탐색기 연결 읽기 중단: {reason}");
            return null;
        }

        /// <summary>
        /// 무엇을 돌려주는지 남기고 그대로 통과시킨다.
        ///
        /// 처음에는 SQL 인증 성공 경로에만 추적을 두었는데, 그 결과 Windows 인증·Entra·
        /// 계정없음 세 경로는 아무 줄도 남기지 않았다. 로그가 "서비스를 얻었습니다"에서 끊기면
        /// 잘 채운 것인지 조용히 죽은 것인지 구분할 방법이 없다 — 그 구분이 이 파일의 존재
        /// 이유이므로, 나가는 문을 하나로 모아 전부 남긴다.
        ///
        /// 문구가 "채택했다"가 아닌 것은 이 타입이 읽기만 하기 때문이다. 호출자는 둘이다 —
        /// 대상을 실제로 채택하는 <c>Connect()</c>와, 선택이 달라졌는지 대조만 하고 아무것도
        /// 바꾸지 않는 <c>CheckSsmsSelection()</c>(마우스가 지나갈 때마다 불린다). 여기서
        /// "채택했다"고 적으면 로그의 대부분이 실제로는 일어나지 않은 채택을 보고하게 된다.
        /// 채택했다는 기록은 채택한 쪽(<c>Connect()</c>)이 남긴다.
        /// </summary>
        private static SsmsConnectionInfo Succeed(SsmsConnectionInfo info)
        {
            SsmsDiagnostics.Trace(info.UnsupportedReason != null
                ? $"개체 탐색기 연결 제한: {info.ServerName}.{info.DatabaseName} — {info.UnsupportedReason}"
                : $"개체 탐색기 연결 읽음: {info.ServerName}.{info.DatabaseName} " +
                  $"{(info.AuthMode == SqlAuthMode.Sql ? "SQL" : "Windows")} 인증, " +
                  $"계정={info.UserName ?? "(없음)"}, 암호 확보={info.Password != null}");
            return info;
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

            var explorer = TryGetObjectExplorerService(explorerServiceType, serviceCacheType);
            if (explorer == null)
            {
                return Fail("어느 공급자에서도 IObjectExplorerService를 얻지 못했습니다.");
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
            // SessionCredentialStore.Set은 공백만 있는 값도 IsNullOrWhiteSpace로 걸러 던진다.
            // 여기서 IsNullOrEmpty만 쓰면 " " 같은 값이 이 관문은 통과했다가 저장소에서
            // 예외로 터진다 — RelayCommand.Execute 안이라 잡을 곳이 없다. 두 층이 같은
            // 판정을 써야 한다.
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                return Fail($"URN에서 데이터베이스를 얻지 못했습니다 (Context='{urn}').");
            }

            var connection = nodeContextType.GetProperty("Connection")?.GetValue(node);
            if (connection == null)
            {
                return Fail("노드의 Connection이 null입니다.");
            }

            var serverName = ReadString(connection, "ServerName");
            // 위 databaseName과 같은 이유로 IsNullOrWhiteSpace를 쓴다 — 리플렉션으로 읽은
            // 값이라 공백만 있는 문자열도 나올 수 있고, 그 값이 그대로 저장소로 가면 예외가 된다.
            if (string.IsNullOrWhiteSpace(serverName))
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
                return Succeed(new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, EntraReason));
            }

            // Authentication도 파생 타입에만 있다. 없으면 이 단계에서는 Entra가 아니라는 뜻이다.
            var authentication = connection.GetType().GetProperty("Authentication")
                ?.GetValue(connection)?.ToString();
            if (authentication != null && authentication.StartsWith("ActiveDirectory", StringComparison.Ordinal))
            {
                return Succeed(new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, EntraReason));
            }

            // 여기까지 왔다면 두 Entra 표지가 모두 없었다는 뜻이므로, 이제야 UseIntegratedSecurity를
            // 믿고 진짜 Windows 통합 인증으로 판정할 수 있다.
            if (ReadBool(connection, "UseIntegratedSecurity"))
            {
                return Succeed(new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Windows, null, null, null));
            }

            var userName = ReadString(connection, "UserName");
            if (string.IsNullOrEmpty(userName))
            {
                // 계정명 없는 SQL 인증 주장은 접속에 쓸 수 없다. 이 값을 그대로 채택 대신
                // NoUserNameReason과 함께 미지원으로 돌려보내는 편이 낫다.
                return Succeed(new SsmsConnectionInfo(
                    serverName!, databaseName!, SqlAuthMode.Sql, null, null, NoUserNameReason));
            }

            return Succeed(new SsmsConnectionInfo(
                serverName!,
                databaseName!,
                SqlAuthMode.Sql,
                userName,
                ReadPassword(connection),
                null));
        }

        private static string? _lastProviderReport;

        /// <summary>
        /// 어느 공급자가 서비스를 줬는지는 <b>바뀔 때만</b> 남긴다.
        ///
        /// 이 조회는 도구 창이 보일 때뿐 아니라 마우스가 패널을 지날 때마다 일어난다.
        /// 매번 적으면 같은 줄이 수십 개 쌓여, 사이에 낀 진짜 사건이 묻힌다.
        /// <see cref="SsmsDiagnostics"/>의 중복 제거는 <i>연속된</i> 같은 줄만 걸러내는데,
        /// 여기서는 조회 결과가 사이에 끼어들어 매번 새 줄로 취급된다.
        ///
        /// 공급자가 바뀌거나 실패로 돌아서면 그때는 남는다 — 그것이 알고 싶은 사건이다.
        /// </summary>
        private static void TraceProvider(string message)
        {
            if (string.Equals(_lastProviderReport, message, StringComparison.Ordinal)) return;
            _lastProviderReport = message;
            SsmsDiagnostics.Trace(message);
        }

        /// <summary>
        /// 개체 탐색기 서비스를 공급자 후보에서 차례로 찾는다.
        ///
        /// <c>ServiceCache.ServiceProvider</c> 하나만 믿었다가 실패했다. 측정된 SSMS 21에서
        /// 그 어셈블리는 아무도 로드하지 않으며 — 즉 셸이 <c>ServiceCache.Init()</c>을 부른 적이
        /// 없으며 — 우리가 강제로 로드해 봤자 static 필드가 비어 있는 사본을 얻을 뿐이다.
        /// SSMS 21에서 <c>ServiceCache</c>는 더 이상 쓰이지 않는 레거시 경로다.
        ///
        /// 그래서 VS 셸의 전역 공급자를 먼저 본다. 이 확장은 이미 <c>Microsoft.VisualStudio.Shell</c>을
        /// 참조하므로 리플렉션 없이 부를 수 있다. 어느 경로가 통했는지는 진단 로그가 남긴다 —
        /// 이 코드는 SSMS 안에서만 실행되므로 그 기록이 유일한 근거다.
        /// </summary>
        private static object? TryGetObjectExplorerService(Type explorerServiceType, Type serviceCacheType)
        {
            try
            {
                var global = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider;
                var service = global?.GetService(explorerServiceType);
                if (service != null)
                {
                    TraceProvider("개체 탐색기 서비스: VS 전역 공급자에서 얻었습니다.");
                    return service;
                }
                TraceProvider(
                    $"VS 전역 공급자가 IObjectExplorerService를 돌려주지 않았습니다 " +
                    $"(공급자={(global == null ? "null" : "있음")}).");
            }
            catch (Exception ex)
            {
                TraceProvider($"VS 전역 공급자 조회 실패: {ex.GetType().Name} — {ex.Message}");
            }

            try
            {
                var cached = serviceCacheType
                    .GetProperty("ServiceProvider", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) as IServiceProvider;
                if (cached == null)
                {
                    TraceProvider("ServiceCache.ServiceProvider가 null입니다(초기화되지 않은 사본).");
                    return null;
                }

                var service = cached.GetService(explorerServiceType);
                TraceProvider(service != null
                    ? "개체 탐색기 서비스: ServiceCache에서 얻었습니다."
                    : "ServiceCache도 IObjectExplorerService를 돌려주지 않았습니다.");
                return service;
            }
            catch (Exception ex)
            {
                TraceProvider($"ServiceCache 조회 실패: {ex.GetType().Name} — {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// SSMS 셸 타입을 찾는다. 로드된 어셈블리를 먼저 뒤지고, 없으면 이름으로 로드를 시도한다.
        ///
        /// 로드된 것만 보면 충분하다고 봤던 것이 틀렸다. 실제 SSMS 21에서 측정한 결과
        /// <c>SqlWorkbench.Interfaces</c>(개체 탐색기 인터페이스)는 로드되어 있는데
        /// <c>Microsoft.SqlServer.SqlTools.VSIntegration</c>(<c>ServiceCache</c>)은 그렇지 않았다 —
        /// 도구 창을 열어 볼 때까지 아무도 그 어셈블리를 건드리지 않기 때문이다.
        /// 그래서 연결 읽기가 첫 관문에서 조용히 멈췄다.
        ///
        /// <see cref="Assembly.Load(AssemblyName)"/>은 SSMS.exe의 기준 디렉터리(IDE 폴더)를 뒤지므로
        /// 설치 경로를 하드코딩하지 않아도 된다. 셸 밖(단위 테스트)에서는 그냥 실패해
        /// 지금까지처럼 <c>null</c>이 된다.
        /// </summary>
        private static Type? FindType(string assemblySimpleName, string typeName)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(
                    a.GetName().Name, assemblySimpleName, StringComparison.OrdinalIgnoreCase));

            if (assembly == null)
            {
                try
                {
                    assembly = Assembly.Load(new AssemblyName(assemblySimpleName));
                }
                catch (Exception ex)
                {
                    // 셸 밖이면 정상적인 결과다. 셸 안이라면 아래 Fail이 사유를 남긴다.
                    Debug.WriteLine($"FindType: '{assemblySimpleName}' 로드 실패 — {ex.Message}");
                    return null;
                }
            }

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
