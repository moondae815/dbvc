# DBVC SSMS 연결 재사용 설계

## 1. Overview

지금 사용자는 같은 접속을 두 번 한다. SSMS 개체 탐색기에서 서버에 접속하고, DBVC 도구 창에서
서버·데이터베이스·인증 방식·계정·암호를 다시 입력한다. DBVC는 SSMS 셸 안에서 동작하면서도
셸이 이미 들고 있는 연결 정보를 전혀 보지 않는다 — 서버 이름조차 가져오지 않는다.

이 문서는 **개체 탐색기에서 선택된 노드의 연결을 DBVC 입력란에 자동으로 채우는** 설계를 다룬다.
가져온 암호는 디스크에 저장하지 않고 프로세스 메모리에만 둔다.

## 2. 측정된 전제

추측이 아니라 이 기계에 설치된 SSMS 21
(`C:\Program Files\Microsoft SQL Server Management Studio 21\Release\Common7\IDE`, 어셈블리 버전 21.200.0.0)의
어셈블리를 리플렉션으로 직접 조사한 결과다.

* `Microsoft.SqlServer.SqlTools.VSIntegration.dll` → `Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache`
  는 정적 속성 `ServiceProvider`를 노출하고, 그 타입은 `System.IServiceProvider`를 구현한다
  (`object GetService(Type)`).
* `SqlWorkbench.Interfaces.dll` → `...ObjectExplorer.IObjectExplorerService`에
  `void GetSelectedNodes(out int, out INodeInformation[])`가 있다.
* **SSMS 21의 `INodeInformation`에는 `Connection`이 없다.** 멤버는
  `Parent`·`Name`·`InvariantName`·`Hierarchy`·`Item[string]`뿐이다. 연결 정보는 형제 인터페이스
  `...ObjectExplorer.INodeContext`에 있고(`Connection`, `Context`, `UrnPath`, `NavigationContext`),
  개체 탐색기가 실제로 돌려주는 구현체(`NodeContext`, `NavigationContext`, `RootContext`,
  `RefreshInformationProxy`)는 **네 가지 모두 두 인터페이스를 함께 구현한다.**
  즉 선택 노드를 `INodeContext`로 캐스팅하는 경로가 성립한다.
* `INodeContext.Connection`의 선언 타입은 `Microsoft.SqlServer.Management.Common.SqlOlapConnectionInfoBase`이며,
  **이 기반 타입 자체가** `ServerName`·`DatabaseName`·`UserName`·`Password`·`SecurePassword`·
  `UseIntegratedSecurity`를 모두 노출한다. 파생 타입 `SqlConnectionInfo`에만 있는 것은
  `Authentication`(`AuthenticationMethod`)과 `AccessToken`이다.
* `AuthenticationMethod`의 값은 `NotSpecified`, `SqlPassword`, 그리고 `ActiveDirectory`로 시작하는
  8개다. Entra ID 계열은 토큰 기반이므로 사용자명·암호로 환원할 수 없다.

**이 전제가 뒤집히면 설계도 뒤집힌다.** SSMS 22에서 `INodeContext`가 사라지거나 `Connection`의
형태가 바뀌면 4.2의 어댑터는 `null`을 돌려주고, DBVC는 자동 채움 없이 지금과 똑같이 동작한다.

## 3. Scope

### In Scope

* 개체 탐색기 선택 노드에서 서버·데이터베이스·인증 방식·계정·암호를 읽는 어댑터
* 도구 창이 보여질 때 자동 채움 + 수동 갱신 버튼
* 세션 전용(메모리 한정) 암호 보관과 그것을 우선 사용하는 암호 조회
* Entra ID 연결처럼 재사용할 수 없는 경우의 안내

### Out of Scope

* **개체 탐색기 선택 변경 이벤트 구독.** 선택이 바뀔 때마다 DBVC 컨텍스트가 따라가는 동작은
  이번 결정(Connect 버튼 유지)에 필요 없다. 자동 채움 시점은 도구 창 가시성과 명시적 버튼으로 충분하다.
  다만 "선택을 바꿨는데 아무 일도 없다"가 버튼 고장과 구분되지 않는다는 문제는 남는다 —
  이벤트 구독이 아니라 시선이 패널로 올 때의 대조로 푼다 (4.6.1).
* **활성 쿼리 편집기 창의 연결 사용**(`ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo`).
  같은 목적을 두 경로로 구현하면 우선순위 규칙과 테스트가 두 배가 된다. 개체 탐색기 하나로 시작한다.
* **Entra ID 연결 지원.** 액세스 토큰을 `Microsoft.Data.SqlClient`로 옮기는 일은 토큰 수명·갱신을
  다뤄야 하는 별도 서브시스템이다. 지금은 감지해서 안내만 한다.
* **SSMS 어셈블리를 컴파일 타임에 참조하는 것.** 4.2에 이유를 적는다.
* **SSMS에서 가져온 암호의 디스크 저장.** 명시적으로 배제한다 (4.3).

## 4. Component Design

### 4.1. 전체 흐름

```
개체 탐색기 선택 노드
   └─ ObjectExplorerConnectionSource (리플렉션)      … DBVC.Vsix
        └─ SsmsConnectionInfo (DTO)
             └─ ViewChangesViewModel.TryFillFromSsms()
                  ├─ ServerName / DatabaseName / AuthMode / UserName  → 입력란
                  └─ 암호는 ViewModel 필드에만 (PasswordBox에 넣지 않는다)
                       └─ Connect
                            ├─ SqlCredentialStore.Save(..., plainPassword: null)   → 디스크: 인증 방식·계정명
                            └─ SqlCredentialStore.SetSessionPassword(...)          → 메모리: 암호
                                 └─ SqlConnectionFactory.Build() 가 세션 암호를 먼저 본다
```

### 4.2. `ISsmsConnectionSource` / `ObjectExplorerConnectionSource` (신규, DBVC.Vsix/Services)

```
public sealed class SsmsConnectionInfo
{
    public string ServerName { get; }
    public string DatabaseName { get; }
    public SqlAuthMode AuthMode { get; }
    public string? UserName { get; }
    public string? Password { get; }

    /// 이 연결을 그대로 재사용할 수 없는 사유. null이면 재사용 가능.
    public string? UnsupportedReason { get; }
}

public interface ISsmsConnectionSource
{
    /// 개체 탐색기의 현재 선택에서 연결을 읽는다. 읽을 수 없으면 null.
    SsmsConnectionInfo? TryGetCurrent();
}
```

ViewModel은 이 인터페이스만 안다. 테스트는 가짜 구현을 주입한다.

**`ServerName`과 `DatabaseName`이 non-null인 이유.** 둘 중 하나라도 확정할 수 없으면
`TryGetCurrent()`가 `null`을 반환한다. "서버는 알지만 DB는 모르는" 절반짜리 결과를 만들지 않는다.
그런 값으로 입력란을 채우면 사용자가 직접 입력해 둔 데이터베이스 이름을 지우게 되고,
DBVC는 데이터베이스 없이는 아무 일도 못 한다.

**리플렉션을 쓰는 이유.** SSMS 어셈블리는 설치 폴더에만 있고 GAC에 없다. 컴파일 타임에 참조하면
(a) 빌드가 특정 SSMS 설치에 묶이고 — 이 저장소는 `$(OS)` 분기로 비Windows에서도 컴파일과 단위
테스트가 돌아간다 — (b) 어셈블리 버전이 21.200.0.0으로 고정되어 SSMS 22에서 로드가 깨진다.
리플렉션은 두 문제를 모두 피하고, 실패가 "자동 채움이 안 됨"으로 국한된다.

조회 순서:

1. `AppDomain.CurrentDomain.GetAssemblies()`에서 단순 이름으로
   `Microsoft.SqlServer.SqlTools.VSIntegration`과 `SqlWorkbench.Interfaces`를 찾고,
   **없으면 `Assembly.Load(단순 이름)`으로 로드를 시도한다.** 그래도 없으면 `null` —
   DBVC.Vsix.Tests처럼 셸 밖에서 도는 경우다.

   **"셸 안에서는 이미 로드되어 있다"는 처음 전제는 틀렸다.** 실제 SSMS 21에서 측정한 결과
   `SqlWorkbench.Interfaces`는 로드되어 있지만 `Microsoft.SqlServer.SqlTools.VSIntegration`은
   그렇지 않았다(도구 창을 열 때까지 아무도 그 어셈블리를 건드리지 않는다). 그래서 자동 채움이
   첫 관문에서 조용히 멈췄다 — 진단 로그가 없었다면 원인을 추정할 수밖에 없었을 종류의 실패다.
   `Assembly.Load`는 SSMS.exe의 기준 디렉터리(IDE 폴더)를 뒤지므로 설치 경로를 하드코딩하지 않는다.
2. `Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider`(컴파일 타임에 이미 참조하는
   타입) → `GetService(typeof(IObjectExplorerService))`. 실패하면 `ServiceCache.ServiceProvider`로
   물러선다.

   **`ServiceCache`를 첫 경로로 삼은 처음 설계는 SSMS 21에서 동작하지 않는다.** 1번의
   `Assembly.Load`로 어셈블리를 얻어도 `ServiceCache.ServiceProvider`는 여전히 `null`이었다 —
   SSMS 21이 그 어셈블리를 로드하지 않는다는 것은 곧 아무도 `ServiceCache.Init()`을 부르지
   않았다는 뜻이고, 강제로 로드한 사본은 초기화되지 않은 빈 껍데기다. `ServiceCache`는 이
   버전에서 사실상 레거시다. VS 전역 공급자는 같은 서비스를 돌려주며, SSMS 21에서 측정으로
   확인했다(`자동 채움: localhost.Northwind SQL 인증, 암호 확보=True`). 두 경로를 모두
   남겨 두는 것은 SSMS 20 이하에서도 동작할 여지를 버리지 않기 위해서다.
3. `GetSelectedNodes`를 `object[] { 0, null }`로 호출하고 out 인자를 회수한다. 노드가 없거나
   **두 개 이상이면 `null`** — 다중 선택에서 어느 것을 뜻하는지 정할 근거가 없다.
4. 노드가 `INodeContext`를 구현하는지 확인하고 `Connection`(객체)과 `Context`(URN 문자열)를 읽는다.
5. `Connection`에서 `ServerName`, `UserName`, `UseIntegratedSecurity`, `Password`(없으면
   `SecurePassword`를 평문으로 환원)를 읽는다. `Authentication`과 `AccessToken` 속성은
   **있으면** 읽는다(기반 타입에는 없다).
6. 데이터베이스 이름은 `Context` URN에서만 얻는다(4.4). 얻지 못하면 `null` 반환.

**`Connection.DatabaseName`으로 폴백하지 않는다.** 서버 노드를 선택하면 URN에 `Database` 마디가
없는데, 이때 `Connection.DatabaseName`은 그 연결의 초기 카탈로그(대개 `master`)를 돌려준다.
폴백을 두면 "사용자가 고르지 않은 데이터베이스를 골라 준 것처럼" 채우게 된다. URN에 데이터베이스가
없다는 것은 사용자가 데이터베이스를 지목하지 않았다는 뜻이므로, 그대로 아무것도 하지 않는 편이 옳다.

**인증 방식 판정**

순서대로 묻는다. 앞 단계에서 걸리면 뒤는 보지 않는다.

1. `AccessToken`(파생 타입에만 존재)이 `null`이 아니면 → `UnsupportedReason` = Entra 사유, 서버·DB만
   채움. 토큰 기반 연결은 사용자명·암호로 환원할 수 없다.
2. 그렇지 않고 `Authentication`(파생 타입에만 존재)이 `ActiveDirectory*`로 시작하면 → 위와 동일하게
   `UnsupportedReason` = Entra 사유.
3. 그렇지 않고 `UseIntegratedSecurity == true`이면 → `AuthMode.Windows`, 암호 없음.
4. 그렇지 않고 `UserName`이 비어 있으면 → `UnsupportedReason` = "계정 정보를 읽지 못함" 사유, 서버·DB만
   채움.
5. 그 밖(사용자명이 있음) → `AuthMode.Sql` + 사용자명 + 암호(있으면).

**이 순서가 계약이다.** 측정된 SSMS 21(`SqlConnectionInfo`, `Microsoft.SqlServer.ConnectionInfo`
17.100) 동작: `UseIntegratedSecurity`는 새 인스턴스에서 기본값이 `true`이고, `UserName`을 설정하는
부수 효과로만 `false`가 된다. `Authentication`을 Entra 계열 값(`ActiveDirectoryPassword`·
`Interactive`·`DeviceCodeFlow`·`ManagedIdentity`·`MSI`·`ServicePrincipal`·`Default` 등)으로
설정해도 `UseIntegratedSecurity`는 그대로 `true`로 남는다 — `ActiveDirectoryIntegrated`만 예외적으로
`false`다. 그래서 `UseIntegratedSecurity`를 먼저 물으면 이런 Entra 연결들이 전부 "Windows 인증,
재사용 가능"으로 오판된다. `AccessToken`은 토큰 기반 연결의 확정적 표지이므로 가장 먼저 걸러내고,
그다음 `Authentication` 문자열로 나머지 Entra 케이스를 걸러낸 뒤에야 `UseIntegratedSecurity`를 믿을
수 있다.

**모든 단계가 실패에 관대하다.** 어느 리플렉션 단계에서든 예외가 나면 `Debug.WriteLine` 후 `null`을
반환한다. 자동 채움이 안 되는 것과 도구 창이 죽는 것은 비교할 문제가 아니다.

**UI 스레드.** `GetSelectedNodes`는 개체 탐색기 트리를 건드리므로 UI 스레드에서 호출해야 한다.
호출 지점(4.6)이 모두 WPF 이벤트 핸들러와 명령이므로 이미 UI 스레드다. 어댑터는 스레드를 전환하지
않고, 이 제약을 XML 주석으로 남긴다.

### 4.3. 세션 전용 암호

SSMS에서 가져온 암호는 디스크에 쓰지 않는다. 그런데 `SqlConnectionFactory.Build()`는 연결 문자열을
만들 때 `ISqlCredentialStore.ResolvePassword()`만 보므로, 세션 암호도 그 경로에 실려야
`StateTracker`와 `SmoManager`가 그대로 쓸 수 있다.

#### 4.3.1. `SessionPasswordCache` (신규, DBVC.Core)

```
public class SessionPasswordCache
{
    /// null 또는 빈 문자열이면 제거한다.
    void Set(string serverName, string databaseName, string? plainPassword);
    string? TryGet(string serverName, string databaseName);
    bool Remove(string serverName, string databaseName);
}
```

`ConcurrentDictionary<string, string>` 하나다. 키는 `SqlCredentialStore`와 같은
`"{server}::{db}"` + `OrdinalIgnoreCase` 규약을 쓴다. 파일도 보호기도 건드리지 않는다.

**별도 클래스로 두는 이유.** `SqlCredentialStore`의 책임은 "파일에 보관"이다. 세션 암호는 수명이
프로세스이고 저장 매체가 없다 — 같은 클래스 안에서 두 수명을 섞으면 직렬화 코드가 "이 항목은
디스크에 쓰면 안 된다"는 분기를 들고 다니게 된다. 분리하면 캐시가 단독으로 테스트되고,
직렬화 경로는 세션 암호의 존재를 아예 모른다.

#### 4.3.2. `ISqlCredentialStore` 확장

```
public interface ISqlCredentialStore
{
    ...
    /// 이 프로세스에서만 유효한 암호를 기록한다. 디스크에 쓰지 않는다.
    /// null 또는 빈 문자열이면 기존 세션 암호를 제거한다.
    void SetSessionPassword(string serverName, string databaseName, string? plainPassword);
}
```

`SqlCredentialStore`가 `SessionPasswordCache`를 합성해서 보유한다(생성자로 주입 가능, 기본값은 새 인스턴스).
`DbvcServices`가 저장소 인스턴스를 하나만 공유하므로 캐시 공유도 자동으로 따라온다.

**`ResolvePassword`의 조회 순서가 바뀐다.**

```
ResolvePassword(credential):
    credential == null                → null
    세션 캐시에 (Server, Database) 있음 → 그 값                    // 신규
    ProtectedPassword 있음             → Unprotect(...)            // 기존
    그 밖                              → null
```

**`Save`가 세션 암호를 무효화하는 경우.** 사용자가 직접 입력한 값이 SSMS에서 가져온 값을 이겨야 한다.

| `Save` 호출 | 세션 캐시 |
| --- | --- |
| `authMode != Sql` | 제거 — Windows 인증으로 되돌렸으므로 |
| `plainPassword`가 비-null (빈 문자열 포함) | 제거 — 사용자가 명시적으로 입력했다 |
| 계정명이 기존 항목과 다름(대소문자 무시, 기존 항목이 있을 때만) | 제거 — 세션 암호는 옛 계정의 것이라 새 계정과 짝지으면 안 된다 |
| `plainPassword == null` ("저장된 것을 그대로 둔다")이고 계정명도 그대로 | 유지 — SSMS 경로가 쓰는 형태 |

마지막 칸이 SSMS 경로다. `plainPassword: null`은 기존 계약상 "디스크 암호를 건드리지 않는다"이므로,
같은 호출로 인증 방식·계정명만 디스크에 남기고 암호는 손대지 않는 동작이 이미 성립한다.
새 의미를 만들 필요가 없다. 다만 SSMS가 이전과 다른 계정으로 연결을 가져온 경우에는 계정명 자체가
바뀌므로 세 번째 칸이 먼저 적용되어 세션 암호가 지워진다 — SSMS 경로는 `Save` 직후 항상
`SetSessionPassword`를 다시 호출하므로 곧바로 새 값으로 채워져 문제가 없다.

#### 4.3.3. 부수 효과

DPAPI를 쓸 수 없는 환경에서도 SSMS 유래 SQL 인증 접속이 가능해진다. 보호할 대상이 디스크에
없기 때문이다. `PersistCredential`의 "암호를 저장하지 못해 접속할 수 없다" 가드는
**사용자 입력 경로에만** 적용된다(4.5).

### 4.4. URN에서 데이터베이스 이름 (신규, DBVC.Vsix/Services)

`INodeContext.Context`는 SMO URN이다.

```
Server[@Name='LOCALHOST\SQL2022']/Database[@Name='AdventureWorks']/Table[@Name='Person'and@Schema='Person']
```

```
internal static class SsmsUrn
{
    internal static string? TryGetDatabaseName(string? urn);
}
```

`Database[@Name='` 다음부터 닫는 작은따옴표까지를 취한다. SMO URN은 값 안의 작은따옴표를 두 번
반복(`''`)으로 이스케이프하므로, 닫는 따옴표는 **뒤따르는 문자가 `'`가 아닌 첫 `'`**이고
회수한 값에서는 `''`를 `'`로 되돌린다. 패턴이 없으면 `null`.

**정규식이 아니라 문자열 스캔인 이유.** 이스케이프 규칙 때문에 정규식이 오히려 읽기 어려워지고,
이 파서는 어댑터에서 순수 함수로 떼어낸 유일한 로직이라 테스트 대상이 명확해야 한다.

### 4.5. `ViewChangesViewModel` 변경

생성자에 `ISsmsConnectionSource? ssmsConnectionSource = null`를 더한다. `null`이면 자동 채움 기능이
꺼진 것과 같고 기존 동작이 그대로 유지된다.

#### 4.5.1. 암호의 출처 추적

`Password`는 지금 자동 속성이다. 백킹 필드를 가진 속성으로 바꾸고 **setter가 출처 플래그를 내린다.**

```
private string? _password;
private bool _passwordFromSsms;

public string? Password
{
    get => _password;
    set { _password = value; _passwordFromSsms = false; }   // 사용자가 입력했다
}
```

`ViewChangesControl.OnSqlPasswordChanged`가 이 setter를 쓰므로, 자동 채움 후 사용자가 PasswordBox에
한 글자라도 치면 그 순간 사용자 입력으로 전환된다. 자동 채움만 백킹 필드에 직접 쓴다.

**출처 플래그는 대상에도 묶인다.** SSMS에서 가져온 암호는 그것을 가져올 당시의
(서버, 데이터베이스, 인증 방식, 계정) 네 가지에만 속한다. 넷 중 하나라도 바뀌면 더 이상 그 암호가
맞는 대상이 아니므로 들고 있어서는 안 된다 — 들고 있으면 Connect가 다른 서버로 그 암호를 보내는
접속을 시도하게 된다. 그래서 `ServerName`·`DatabaseName`·`AuthMode`·`UserName` 네 setter가 모두
(값이 실제로 바뀔 때) `ForgetSsmsPassword()`를 호출해 `_password`·`_passwordFromSsms`·
`ConnectionSourceMessage`를 함께 정리한다. 사용자가 직접 입력한 암호는 이 경로를 타지 않으므로
건드리지 않는다.

#### 4.5.2. `TryFillFromSsms()`

```
public bool TryFillFromSsms()
{
    var info = _ssmsConnectionSource?.TryGetCurrent();
    if (info == null) return false;

    // 사용자가 입력 중인 암호를 지우지 않는다.
    if (!_passwordFromSsms && !string.IsNullOrEmpty(_password)) return false;

    ServerName = info.ServerName;        // setter가 ForgetSsmsPassword() 후 LoadSavedCredential()을 부른다
    DatabaseName = info.DatabaseName;

    if (info.UnsupportedReason != null)
    {
        // 대상이 바뀌었다면 위 두 setter가 이미 SSMS 암호를 버렸다. 배너만 여기서 한 번 더 내린다 —
        // 대상이 그대로인데 지원 여부만 바뀐 경우는 setter가 호출되지 않기 때문이다.
        ConnectionSourceMessage = null;
        WarningMessage = info.UnsupportedReason;
        return true;
    }

    AuthMode = info.AuthMode;            // 저장소 값을 SSMS 값으로 덮어쓴다
    UserName = info.UserName;
    _password = info.Password;
    _passwordFromSsms = info.Password != null;
    ConnectionSourceMessage = ...;
    return true;
}
```

**순서가 계약이다.** `ServerName`·`DatabaseName` setter는 `LoadSavedCredential()`을 호출해
`AuthMode`와 `UserName`을 저장소 값으로 덮어쓴다. 그러므로 서버·DB를 **먼저** 넣고 SSMS 값을
**나중에** 얹어야 한다. 반대로 하면 SSMS에서 가져온 인증 정보가 디스크의 옛 값으로 되돌아간다.
이 순서를 지키는 테스트를 둔다.

**서버·DB가 바뀌면 활성 컨텍스트도 무효화한다.** `ServerName`·`DatabaseName` setter는
`InvalidateActiveContext()`를 함께 호출해 `Changes`·`SelectedChange`·`IsMapped`·`IsInitialized`·
`WarningMessage`를 비운다. 자동 채움이 없던 시절에는 대상이 사용자의 타이핑으로만 바뀌었지만,
이제는 도구 창을 여는 것만으로 바뀔 수 있다. 그대로 두면 화면에는 A 데이터베이스의 변경 목록이
남아 있는데 입력란은 B를 가리키게 되고, 커밋이 엉뚱한 대상으로 나간다.

**`ConnectionSourceMessage`(신규, 읽기 전용 바인딩).** PasswordBox는 비어 있는데 암호는 실려 있는
상태가 되므로, 그 사실을 한 줄로 알린다: `"SSMS 개체 탐색기 연결에서 가져왔습니다 (암호 포함). Connect를 누르세요."`
암호가 없으면 `(암호 포함)`을 뺀다. 자동 채움이 없었으면 `null`이고 UI에서 숨는다.

**SSMS 암호를 PasswordBox에 넣지 않는 이유.** 넣으면 `Password` setter를 타서 출처 플래그가
내려가고, 결과적으로 디스크에 저장된다 — 이번 결정과 정반대다. 길이가 노출되는 문제도 따라온다.

#### 4.5.3. `PersistCredential()` 분기

```
if (_passwordFromSsms && AuthMode == SqlAuthMode.Sql)
{
    _credentialStore.Save(ServerName!, DatabaseName!, AuthMode, UserName, null);  // 암호는 건드리지 않음
    _credentialStore.SetSessionPassword(ServerName!, DatabaseName!, _password);
    return true;
}
// 기존 경로: Save(..., _password) + fullySaved 가드
```

`AuthMode == SqlAuthMode.Sql` 조건은 2차 방어선이다. 정상적인 흐름에서는 4.5.1의 네 setter가 대상이나
인증 방식이 바뀌는 순간 이미 `_passwordFromSsms`를 내린다. 이 조건이 없어도 지금은 항상 거짓/참이
일치하지만, 앞으로 그 setter들 중 하나가 잘못 고쳐져 더 이상 플래그를 내리지 않게 되더라도 이
조건이 있으면 Windows 인증으로 표시된 대상에 SQL 암호가 조용히 쓰이는 일만은 막는다.

`finally`에서 `_password = null; _passwordFromSsms = false; ConnectionSourceMessage = null;`로 정리한다.
평문은 지금처럼 ViewModel에 남지 않는다 — 세션 캐시가 들고 있다. 배너까지 내리는 이유는 접속을
확정한 뒤에는 "가져왔습니다" 안내가 더 이상 현재 상태를 설명하지 않기 때문이다.

#### 4.5.4. `RefreshFromSsmsCommand` (신규)

`TryFillFromSsms()`를 호출하는 명령. `CanExecute`는 소스 주입 여부. 도구 창을 개체 탐색기와 나란히
띄워 두면 가시성 이벤트가 뜨지 않으므로(4.6), 결정적인 수동 갱신 수단이 하나 필요하다.

### 4.6. 자동 채움 시점 (`ViewChangesControl`)

`IsVisibleChanged`에서 보여지는 쪽으로 바뀔 때 `TryFillFromSsms()`를 호출한다. 도구 창을 처음 열
때와 다른 탭에서 돌아올 때를 모두 덮는다. 항상 보이도록 도킹해 둔 경우는 4.5.4의 버튼이 담당한다.

반환값은 무시한다 — 실패는 "입력란이 그대로"라는 정상 상태다.

#### 4.6.1. 선택이 달라졌음을 알리는 안내 (`CheckSsmsSelection`)

도구 창이 **계속 보이는 채로** 개체 탐색기 선택만 바뀌면 `IsVisibleChanged`는 뜨지 않는다.
사용자가 다른 데이터베이스를 고르고 DBVC를 봤을 때 입력란이 그대로인 것이 이 상태다 —
설계대로지만, 화면에는 "버튼이 고장 났다"와 구분되지 않는다.

선택 변경 이벤트를 구독하지 않는다는 결정(Out of Scope)은 유지한다. 대신 사용자가 이 패널로
시선을 옮기는 순간(`MouseEnter`·`GotKeyboardFocus`)에 개체 탐색기 선택을 한 번 읽어 현재
입력란과 대조하고, 다르면 `SsmsHintMessage`에 한 줄을 띄운다. **입력란은 건드리지 않는다** —
지나가던 마우스가 입력 중이던 값을 덮어쓰면 버튼을 유지하기로 한 결정이 무의미해진다.

전제가 하나 있다: 이 세션에서 자동 채움이 한 번이라도 성공했을 때만 대조한다. 개체 탐색기를
쓰지 않고 직접 입력만 하는 사용자에게는 "선택이 다릅니다"가 참이면서도 아무 의미가 없고,
패널을 볼 때마다 뜨는 배너는 읽히지 않은 채 진짜 경고까지 같이 묻히게 만든다.

고를 수 없는 선택(선택 없음·다중 선택·서버 노드)은 `null`이 돌아오므로 "달라졌다"고 말하지
않고 안내를 내린다. 개체 탐색기에서 잠깐 다른 것을 클릭했다고 배너가 뜨면 안 된다.

`SsmsHintMessage`는 `ConnectionSourceMessage`와 따로 둔다. 그쪽은 "지금 입력란에 있는 값이
어디서 왔는가"를 말하는 사후 보고이고, 이쪽은 "무언가 하려면 무엇을 눌러야 하는가"를 말하는
행동 안내다. 두 문장은 동시에 참일 수 있다 — 가져온 값을 들고 있는 채로 개체 탐색기 선택만
옮겨 간 상태가 그렇다. 암호 칸에 입력값이 남아 자동 채움을 건너뛴 경우도 여기에 싣는다.

#### 4.6.2. 가져온 암호의 표시 전용 마스킹

암호 칸이 비어 있는데 암호는 실려 있는 상태는 사용자에게 "다시 입력해야 하나"로 읽힌다.
그렇다고 SSMS 암호를 `PasswordBox`에 넣을 수는 없다 — `Password` setter를 타는 순간 출처
표시가 풀려 그 암호가 디스크로 새어 나간다(4.5.2).

그래서 같은 칸 **위에** 고정 8자 마스킹을 덮는다(`IsHitTestVisible="False"`, `HasSsmsPassword`
바인딩). 클릭·툴팁은 그대로 `PasswordBox`가 받고, 한 글자라도 입력하면 `Password` setter가
출처 표시를 내려 마스킹이 사라진다. 길이를 고정하는 것은 실제 암호 길이를 노출하지 않기
위해서다.

`HasSsmsPassword`가 백킹 필드가 아니라 알림을 올리는 속성을 거치는 이유: 출처 플래그의 대입
지점이 네 곳으로 흩어져 있어, 각 지점에서 알림을 따로 올리게 두면 언젠가 하나가 빠지고
마스킹이 화면에 남는다.

### 4.7. `SqlCredentialException` 메시지 보강

현재 문구는 "Connect에서 사용자명과 암호를 다시 입력하세요"만 안내한다. 이제 대안이 하나 늘었으므로
한 줄을 덧붙인다: SSMS 개체 탐색기에서 해당 데이터베이스를 선택한 뒤 DBVC 창을 다시 열면 연결을
그대로 가져온다는 안내다.

### 4.8. `DbvcServices` 배선

`CreateViewChangesViewModel`이 `new ObjectExplorerConnectionSource()`를 넘긴다. 어댑터의 생성자는
아무 일도 하지 않고 조회 시점에만 리플렉션을 시도하므로, 셸 밖(단위 테스트, 비Windows)에서도
생성 자체는 안전하다.

## 5. Error Handling

| 상황 | 결과 |
| --- | --- |
| SSMS 어셈블리를 못 찾음 (셸 밖) | `TryGetCurrent()` → `null`. 자동 채움 없음, 기존 동작 |
| 개체 탐색기 선택 없음 / 다중 선택 | `null`. 입력란 그대로 |
| 선택 노드가 데이터베이스 하위가 아님 (서버 노드 등) | `null`. 서버만 채워 사용자의 DB 입력을 지우는 일은 없다 |
| Entra ID 연결 | 서버·DB만 채우고 경고 배너에 사유. 인증란은 사용자가 채운다 |
| SQL 인증인데 SSMS가 암호를 안 들고 있음 | 암호 없이 채움 → Connect 시 `plainPassword: null` → 디스크 암호로 폴백 → 그것도 없으면 기존 `SqlCredentialException`(4.7의 보강된 문구) |
| 리플렉션 중 예외 | `Debug.WriteLine` 후 `null`. 도구 창은 영향받지 않는다 |
| 사용자가 암호를 입력 중일 때 자동 채움 시점 도래 | 건너뛴다. 입력 중인 값이 우선. 왜 건너뛰었는지 `SsmsHintMessage`에 남긴다 (4.6.1) |
| 도구 창이 보이는 채로 개체 탐색기 선택만 바뀜 | 입력란은 그대로. 다음에 패널로 시선이 올 때 안내만 띄운다 (4.6.1) |

## 6. Testing Strategy

**단위 테스트 (SSMS 없이)**

* `SsmsUrn.TryGetDatabaseName` — 정상 URN, 서버 노드 URN(`Database` 없음), 이스케이프된 작은따옴표를
  포함한 DB 이름, `null`, 빈 문자열, 형식이 깨진 문자열
* `SessionPasswordCache` — 설정·조회·제거, 대소문자 무시 키, `null`/빈 문자열이 제거로 동작하는지
* `SqlCredentialStore` — 세션 암호가 디스크 암호보다 우선하는지, `Save`가 평문을 받으면 세션 암호가
  사라지는지, `plainPassword: null`이면 유지되는지, Windows 인증 전환 시 사라지는지,
  **그리고 세션 암호가 credentials.json 파일 내용에 나타나지 않는지**(파일을 직접 읽어 검증한다 —
  이것이 이번 결정의 핵심 계약이다)
* `SqlConnectionFactory` — 디스크 암호 없이 세션 암호만 있을 때 SQL 인증 연결 문자열이 만들어지는지
* `ViewChangesViewModel` (가짜 `ISsmsConnectionSource`)
  * 채움 순서 — 저장소에 다른 계정이 저장된 상태에서 `TryFillFromSsms()` 후 SSMS 값이 남아 있는지
    (4.5.2의 순서 계약)
  * 소스가 `null`을 주면 입력란이 바뀌지 않는지
  * `UnsupportedReason`이 있으면 서버·DB만 채우고 경고가 뜨는지
  * 사용자가 암호를 입력한 상태에서는 자동 채움이 건너뛰어지는지
  * Connect 시 `Save`가 `plainPassword: null`로 불리고 `SetSessionPassword`가 불리는지
  * 사용자 입력 경로는 기존대로 `Save(..., 평문)`을 타는지

**단위 테스트로 덮이지 않는 것 (명시)**

`ObjectExplorerConnectionSource`의 리플렉션 자체는 SSMS 프로세스 밖에서 검증할 수 없다.
그래서 이 클래스에 판단 로직을 두지 않는다 — URN 파싱은 4.4로, 인증 방식 판정 표는 얇은 분기로
유지하고, 나머지는 속성 읽기뿐이다. 검증은 아래 수동 절차가 담당한다.

**수동 검증 (SSMS 21)**

1. 개체 탐색기에서 SQL 인증으로 서버에 접속하고 데이터베이스 노드를 선택 → DBVC 창을 연다 →
   서버·DB·인증 방식·계정이 채워지고 "암호 포함" 안내가 뜬다
2. Connect → 접속 성공, 변경 목록이 뜬다
3. `%APPDATA%\DBVC\credentials.json`을 열어 **해당 항목에 암호 필드가 없는지** 확인
4. SSMS를 재시작하고 개체 탐색기에 접속하지 않은 채 Connect → `SqlCredentialException`의 보강된 안내가 뜬다
5. Windows 인증 연결에서도 서버·DB가 채워지고 접속되는지
6. 도구 창을 개체 탐색기와 나란히 띄운 상태에서 다른 DB를 선택 → 갱신 버튼으로 값이 바뀌는지
7. Entra ID로 접속한 서버를 선택했을 때 경고가 뜨고 도구 창이 정상 동작하는지 (가능한 경우)

## 7. 기존 코드에 미치는 영향

* `ISqlCredentialStore`에 `SetSessionPassword`가 추가된다. 테스트는 Moq를 쓰므로 대역 수정은 없다.
* `SqlCredentialStore.ResolvePassword`와 `Save`의 동작이 4.3.2대로 확장된다. 세션 암호가 없을 때의
  동작은 지금과 완전히 같다.
* `ViewChangesViewModel.Password`가 자동 속성에서 백킹 필드 속성으로 바뀐다. 외부에서 본 계약은 같다.
* `SqlConnectionFactory`, `StateTracker`, `SmoManager`, `GitManager`는 **바뀌지 않는다.**
  세션 암호는 저장소 뒤에 숨어 있다.
* `ViewChangesControl.xaml`에 갱신 버튼 하나와 안내 텍스트 한 줄이 추가된다.
