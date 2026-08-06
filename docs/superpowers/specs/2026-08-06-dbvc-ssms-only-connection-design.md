# DBVC 연결 정보 입력란 제거 설계

## 1. Overview

DBVC의 Connect 패널은 지금 `Server` · `Database` · 인증 방식 콤보 · `User` · `Password` 다섯 개의
입력란을 들고 있다. 그런데 [SSMS 연결 재사용](2026-08-05-dbvc-ssms-connection-reuse-design.md)이
들어가면서 이 다섯 값은 모두 개체 탐색기에서 자동으로 온다. 사용자가 직접 칠 일이 없는 입력란이
다섯 개 남아 있는 상태다.

이 문서는 **입력란을 전부 없애고, 접속 대상과 인증 정보가 오직 SSMS 개체 탐색기에서만 오도록**
바꾸는 설계를 다룬다. 사용자가 암호를 칠 경로가 사라지므로 디스크에 자격증명을 보관하는 계층
전체(`credentials.json` · DPAPI 보호기 · 직렬화)도 함께 폐지한다.

이 문서는 위 SSMS 연결 재사용 설계의 4.5·4.6절과 코어 엔진 설계의 자격증명 저장 부분을
**대체한다.** 그 문서들은 당시의 결정 기록으로 남기고 수정하지 않는다.

## 2. 전제

* **SQL 인증 서버에서 개체 탐색기가 암호까지 내준다는 것을 실제 SSMS 21에서 확인했다.** 이것이
  이 설계의 성립 조건이다. 개체 탐색기가 암호를 내주지 않는 환경에서는 접속할 방법이 아예
  없어진다 — 우회 입력이 사라지기 때문이다.
* DBVC는 SSMS 셸 안에서만 동작한다. 개체 탐색기는 항상 존재한다.
* `ObjectExplorerConnectionSource`(리플렉션 어댑터)와 `SsmsConnectionInfo`는 그대로 쓴다.
  이 설계는 그 어댑터가 돌려준 값을 **어떻게 쓰는가**만 바꾼다.

## 3. Scope

### In Scope

* Connect 패널의 모든 입력란 제거와 읽기 전용 대상 표시로의 교체
* `개체 탐색기에서 가져오기` + `Connect` 두 버튼의 `Connect` 하나로의 통합
* `ViewChangesViewModel`에서 암호 출처 추적·인증 방식 선택·저장된 자격증명 복원 로직 제거
* `ISqlCredentialStore`의 메모리 전용 축소와 디스크 구현·DPAPI 보호기·직렬화 삭제
* 남아 있는 `%APPDATA%\DBVC\credentials.json`의 일회성 삭제
* README와 설치 체크리스트의 관련 서술 갱신

### Out of Scope

* **`ObjectExplorerConnectionSource`의 리플렉션 로직 변경.** 읽는 쪽은 그대로다.
* **개체 탐색기 선택 변경 이벤트 구독.** 이전 결정을 유지한다 — 패널로 시선이 올 때의 대조로 푼다.
* **Entra ID 연결 지원.** 여전히 감지해서 안내만 한다. 다만 이제는 접속 시도 자체를 하지 않는다 (4.3.3).
* **활성 쿼리 편집기 창의 연결 사용.** 개체 탐색기 하나로 유지한다.
* **자동 접속.** Connect는 SMO로 전체 객체를 추출해 작업 트리에 `.sql` 파일을 쓰므로,
  마우스가 지나가거나 창이 보이는 것만으로 그 작업이 돌아서는 안 된다. 명시적인 버튼을 유지한다.

## 4. Component Design

### 4.1. 전체 흐름

```
[Connect] 클릭
   └─ ISsmsConnectionSource.TryGetCurrent()
        ├─ null                   → 경고만 남기고 끝. 접속하지 않는다
        ├─ UnsupportedReason 있음 → 대상 표시만 갱신, 경고에 사유. 접속하지 않는다
        └─ 정상
             ├─ ServerName / DatabaseName / AuthMode / UserName ← info   (표시 전용)
             ├─ ISqlCredentialStore.Set(server, db, authMode, userName, password)   … 메모리
             └─ ApplyContext()
                  ├─ StateTracker.TestConnection()   → 실패하면 경고에 사유
                  ├─ ConfigManager.TryGetMapping()   → IsMapped
                  ├─ StateTracker.IsInitialized()    → IsInitialized
                  └─ 둘 다 참이면 Refresh()
```

디스크는 이 흐름의 어디에도 등장하지 않는다.

### 4.2. `ViewChangesControl.xaml` — 화면

**바뀐 뒤의 상단 영역**

```
[Connect]  localhost.Northwind — SQL 인증 (sa)
노랑: 개체 탐색기 선택이 다릅니다 — B.db2. Connect를 누르면 이 대상으로 전환됩니다.
(그 아래 노란 경고 배너 · [저장소 연결...] 버튼은 그대로)
```

* `Server` · `Database` 텍스트 상자, 인증 방식 콤보, `User` 텍스트 상자, `PasswordBox`와
  그 위의 표시 전용 마스킹 `TextBlock`, `개체 탐색기에서 가져오기` 버튼을 모두 제거한다.
* 남는 것은 `Connect` 버튼 하나와 `TargetSummary`를 바인딩한 읽기 전용 `TextBlock`이다.
* 입력란이 사라지면서 좁게 도킹할 때 잘릴 요소가 없어지므로 이 줄의 `WrapPanel`은 유지하되
  자식이 둘로 줄어든다. 아래쪽 Top Area(Refresh·Commit 등)의 `WrapPanel`은 그대로 필요하다.
* `ConnectionSourceMessage`를 바인딩하던 초록 줄을 제거한다 (4.3.2).

**`ViewChangesControl.xaml.cs`**

* `OnSqlPasswordChanged` 제거 — `PasswordBox`가 없다.
* `OnIsVisibleChanged`는 `TryFillFromSsms()` 대신 `CheckSsmsSelection()`을 부른다.
  채울 입력란이 없어졌으므로 가시성 이벤트가 할 수 있는 일은 안내 갱신뿐이다.
* `MouseEnter` · `GotKeyboardFocus` → `CheckSsmsSelection()`은 그대로.
* 구독을 `Unloaded`에서 해제하지 않는 이유(재도킹)와 `Loaded`에서 재구독하는 짝은 그대로 유지한다.

### 4.3. `ViewChangesViewModel`

#### 4.3.1. 남는 연결 관련 표면

```csharp
public string? ServerName   { get; private set; }   // 지금 접속을 시도한 대상
public string? DatabaseName { get; private set; }
public SqlAuthMode AuthMode { get; private set; }   // 표시용
public string? UserName     { get; private set; }   // 표시용

/// "localhost.Northwind — SQL 인증 (sa)" 또는 "(접속되지 않음)"
public string TargetSummary { get; }

public ICommand ConnectCommand { get; }             // 유일한 연결 명령
public string? SsmsHintMessage { get; }             // 개체 탐색기 관련 안내 한 줄
public void CheckSsmsSelection();
```

`ServerName`·`DatabaseName`이 `public`으로 남는 이유는 `ViewChangesControl`이 Diff를 그릴 때
읽기 때문이다(`OnSelectionChanged`). setter만 닫는다.

**`TargetSummary`가 말하는 것은 "Connect가 마지막으로 채택한 대상"이다.** 접속 성공 여부는
말하지 않는다 — 실패는 경고 배너가, 접속되었을 때만 뜨는 변경 목록이 각각 담당하고 있어서,
표시줄까지 같은 사실을 반복하면 세 곳을 동시에 맞춰야 한다. 아직 한 번도 누르지 않았으면
`"(접속되지 않음)"`이다.

**네 값은 이제 항상 함께 바뀐다.** 이전에는 사용자가 서버만 고치고 계정은 그대로 둘 수 있었기
때문에 각 setter가 `ForgetSsmsPassword()`로 암호의 유효성을 따로 지켜야 했다. 이제 넷은
`SsmsConnectionInfo` 하나에서 통째로 들어오므로 그 방어가 필요 없다 — 대상이 바뀐다는 것은
인증 정보도 함께 교체된다는 뜻이다.

#### 4.3.2. 제거되는 것

| 제거 대상 | 이유 |
| --- | --- |
| `Password`, `_password` | 입력 경로가 없다 |
| `PasswordFromSsms`, `HasSsmsPassword` | 암호의 출처가 하나뿐이라 구분할 것이 없다 |
| `ForgetSsmsPassword()` | 4.3.1 — 네 값이 통째로 교체된다 |
| `CanPersistPasswords` | 디스크에 쓰지 않는다 |
| `AuthModes`, `AuthModeOption`, `IsSqlAuth` | 고를 콤보가 없다 |
| `LoadSavedCredential()` | 복원할 디스크 값이 없다 |
| `RefreshFromSsmsCommand` | `Connect` 하나로 합쳐졌다 |
| `TryFillFromSsms()` | `Connect()`에 흡수 |
| `ConnectionSourceMessage`, `HasConnectionSourceMessage` | 사후 보고할 "입력란"이 없다. 대상 표시줄이 그 역할을 흡수한다 |
| `_ssmsFillEverSucceeded` | `CheckSsmsSelection`의 전제였다 — 직접 입력만 하는 사용자가 더는 존재하지 않으므로 무조건 대조한다 |
| `TraceSave()` | 디스크 쓰기 결과를 확인할 파일이 없다. 접속 로그는 `Connect()`가 한 줄로 남긴다 |
| `SetContext(server, db)` 의 public 노출 | 외부 호출자가 없다. 아래 `ApplyContext()`로 대체 |

#### 4.3.3. `Connect()`

```csharp
private void Connect()
{
    var info = _ssmsConnectionSource?.TryGetCurrent();

    if (info == null)
    {
        // 접속을 시도하지 않는다. 대상이 무엇인지 모르는 채로 할 수 있는 일이 없다.
        // 다만 지금 대상과 목록은 그대로 둔다 — 읽지 못했다는 사실은 그것들을
        // 거짓으로 만들지 않는다. 지우면 손이 미끄러진 클릭 한 번에 작업 상태가 날아간다.
        WarningMessage = "개체 탐색기에서 데이터베이스(또는 그 하위 개체)를 하나 선택한 뒤 " +
                         "다시 누르세요. 서버 노드나 여러 개를 선택한 상태에서는 대상을 정할 수 없습니다.";
        return;
    }

    SetTarget(info.ServerName, info.DatabaseName, info.AuthMode, info.UserName);

    if (info.UnsupportedReason != null)
    {
        WarningMessage = info.UnsupportedReason;
        return;   // 접속하지 않는다
    }

    _credentialStore.Set(info.ServerName, info.DatabaseName, info.AuthMode, info.UserName, info.Password);
    SsmsDiagnostics.Trace(
        $"접속 시도: {info.ServerName}.{info.DatabaseName} {info.AuthMode} 인증, " +
        $"계정={info.UserName ?? "(없음)"}, 암호 실림={info.Password != null}");

    ApplyContext();
}
```

`CanExecute`는 `_ssmsConnectionSource != null`이다. 이전의 `HasContext`(서버·DB가 채워졌는가)는
누를 때까지 대상을 모르므로 판정할 수 없다.

**`UnsupportedReason`일 때 접속하지 않는 것이 이전과 다른 점이다.** 예전에는 서버·DB만 채우고
사용자가 인증란을 마저 채우게 했다. 이제 마저 채울 칸이 없으므로, 실패가 확정된 접속을 시도해
`TestConnection`의 낮은 수준 오류를 배너에 흘리는 대신 사유를 그대로 보여주고 멈춘다.

**`SetTarget`은 네 값을 한 번에 대입하고 `InvalidateActiveContext()`를 부른다.** 화면에 남아 있던
이전 대상의 변경 목록이 새 대상의 것으로 오인되면 커밋이 엉뚱한 대상으로 나간다 — 기존
설계에서 이미 확인된 결함이므로 그 방어는 그대로 유지한다. 값이 같아도 무효화한다: 같은 대상으로
다시 Connect하는 것은 "지금 상태를 다시 판정해 달라"는 뜻이다.

#### 4.3.4. `ApplyContext()`

기존 `SetContext`에서 자격증명 저장 부분(`PersistCredential`)을 뺀 나머지다. 접속 판정 →
매핑 판정 → 초기화 판정 → `Refresh`. `ConnectRepository`가 매핑을 추가한 뒤 재판정할 때도
이것을 부른다 — 그 경로는 자격증명을 다시 쓸 필요가 없으므로 오히려 정확해진다.

`PersistCredential()`은 사라진다. 저장 실패라는 개념이 없어졌기 때문이다(메모리 사전 대입은
실패하지 않는다). 그와 함께 "암호를 안전하게 저장하지 못했습니다(DPAPI를 사용할 수 없습니다)"
경고 경로도 사라진다.

#### 4.3.5. `CheckSsmsSelection()`

전제(`_ssmsFillEverSucceeded`)를 없애고 세 갈래로 정리한다.

| 개체 탐색기 선택 | 접속 상태 | `SsmsHintMessage` |
| --- | --- | --- |
| 읽을 수 없음 (`null`) | 접속 전 | `"개체 탐색기에서 데이터베이스를 하나 선택한 뒤 Connect를 누르세요."` |
| 읽을 수 없음 (`null`) | 접속됨 | `null` — 잠깐 다른 노드를 클릭한 것과 구분되지 않는다 |
| 현재 대상과 같음 | 무관 | `null` |
| 현재 대상과 다름 | 접속 전 | `"개체 탐색기 선택: {server}.{db} — Connect를 누르세요."` |
| 현재 대상과 다름 | 접속됨 | `"개체 탐색기 선택이 다릅니다 — {server}.{db}. Connect를 누르면 이 대상으로 전환됩니다."` |

접속 전에도 안내를 내는 것이 이전과 다른 점이다. 입력란이 있던 시절에는 이 안내가 직접 입력하는
사용자에게 무의미한 소음이었지만, 이제는 **Connect가 무엇을 할지 미리 보여주는 유일한 수단**이다.

`CheckSsmsSelection`은 개체 탐색기 트리를 읽으므로 UI 스레드에서만 불린다. 호출 지점이 모두
WPF 이벤트 핸들러라는 기존 제약은 그대로다.

### 4.4. Core — 자격증명 계층

#### 4.4.1. 삭제

| 파일 | 이유 |
| --- | --- |
| `SqlCredentialStore.cs` | 디스크 보관이 유일한 책임이었다 |
| `DpapiPasswordProtector.cs` | 보호할 대상이 디스크에 없다 |
| `IPasswordProtector.cs` | 구현이 하나뿐이었고 그것이 사라진다 |
| `Models/SqlCredentialSerializer.cs` | 직렬화 대상이 없다 |
| `SessionPasswordCache.cs` | 새 저장소에 흡수 |

#### 4.4.2. `ISqlCredentialStore` 축소

```csharp
/// <summary>
/// (서버, 데이터베이스)별 SQL 접속 인증 정보를 이 프로세스가 사는 동안만 보관한다.
/// 디스크에 쓰지 않는다 — 값의 출처는 SSMS 개체 탐색기뿐이고, SSMS가 닫히면 함께 사라진다.
/// </summary>
public interface ISqlCredentialStore
{
    SqlCredential? TryGet(string serverName, string databaseName);
    void Set(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? password);
}
```

사라지는 멤버와 이유:

| 멤버 | 이유 |
| --- | --- |
| `CanPersistPasswords` | 디스크가 없다 |
| `FilePath` | 디스크가 없다 |
| `LastSaveError` | 삼킬 쓰기가 없다 |
| `SetSessionPassword` | 모든 암호가 세션 암호다 — 별도 경로가 아니다 |
| `ResolvePassword` | 복호화할 것이 없다. `credential.Password`를 그대로 읽는다 |
| `Save`의 `bool` 반환 | 실패할 수 있는 저장이 아니다 → `void Set` |
| `Remove` | 부르는 곳이 없다. 디스크 파일에서 항목을 지울 필요가 있던 시절의 잔재이고, 지금은 프로세스가 끝나면 통째로 사라진다 |

`Save` → `Set`으로 이름이 바뀌는 이유는 계약이 달라졌기 때문이다. `Save(…, plainPassword: null)`은
"저장된 암호를 그대로 둔다"였다 — 디스크에 이전 값이 있다는 전제 위에서만 뜻이 있는 규칙이다.
`Set`은 그런 병합을 하지 않고 네 값을 통째로 덮어쓴다. 옛 이름을 남기면 사라진 의미를 계속
암시하게 된다.

#### 4.4.3. `SessionCredentialStore` (신규, DBVC.Core)

`ConcurrentDictionary<string, SqlCredential>` 하나다. 키 규약은 기존과 같은
`"{server}::{db}"` + `OrdinalIgnoreCase`. 파일 입출력도 잠금도 없다.

**파일을 만지지 않는 것이 이 클래스의 계약이다.** 옛 파일 삭제(4.4.5)를 여기 두지 않는 이유가
그것이다 — 넣는 순간 단위 테스트가 디스크를 건드리게 되고, "이 저장소는 디스크를 모른다"는
문장이 거짓이 된다.

#### 4.4.4. `SqlCredential`

`ProtectedPassword`(보호된 문자열) → `Password`(평문)로 바꾼다. 직렬화 속성과 관련 주석은 사라진다.

**평문이 메모리에 사는 것은 새 위험이 아니다.** 지금도 `SessionPasswordCache`가 정확히 그렇게
하고 있고, 오히려 DPAPI로 암호화된 사본이 디스크에서 사라지는 만큼 노출면은 줄어든다.
다만 `SqlCredential`이 로그에 실리면 평문이 함께 나가므로, **`ToString()`을 재정의하지 않고
진단 로그에는 `Password != null` 여부만 남긴다** (4.3.3의 `SsmsDiagnostics.Trace`가 그 형태다).

#### 4.4.5. `LegacyCredentialFile` (신규, DBVC.Core)

```csharp
internal static class LegacyCredentialFile
{
    /// %APPDATA%\DBVC\credentials.json 이 남아 있으면 지운다. 실패는 삼킨다.
    internal static void DeleteIfPresent(string? path = null);
}
```

기존 사용자의 파일에는 DPAPI로 암호화된 암호가 남아 있다. 아무도 읽지 않는 파일이 되지만
"디스크에 자격증명을 남기지 않는다"는 이번 결정과 어긋난 채 방치된다. DBVC가 만든 파일이므로
지운다.

* **디렉터리는 건드리지 않는다.** 같은 폴더에 `mappings.json`이 있다.
* **실패는 삼킨다.** 파일을 지우지 못하는 것과 플러그인이 뜨지 않는 것은 비교할 문제가 아니다.
  `Debug.WriteLine`으로 사유만 남긴다.
* **호출 지점은 `DbvcServices` 생성자다.** 합성 루트가 일회성 정리의 자리다.
  멱등이므로 생성자가 여럿이어도 중복 호출이 무해하다.
* 테스트를 위해 경로를 주입받는다. 기본값은 옛 `SqlCredentialStore.DefaultFilePath`와 같은 경로다.

### 4.5. `SqlConnectionFactory`

```csharp
var credential = _credentialStore.TryGet(serverName, databaseName);

if (credential == null || credential.AuthMode != SqlAuthMode.Sql)
    return BuildWindows(serverName, databaseName);

if (string.IsNullOrEmpty(credential.UserName) || string.IsNullOrEmpty(credential.Password))
    throw new SqlCredentialException(
        $"'{serverName}.{databaseName}'은(는) SQL 인증으로 설정되어 있으나 암호를 사용할 수 없습니다. " +
        "SSMS 개체 탐색기에서 이 데이터베이스에 접속한 뒤 DBVC 창에서 Connect를 누르세요. " +
        "(인증 정보는 SSMS를 닫으면 사라지므로 재시작 후에는 다시 눌러야 합니다.)");

return BuildSql(serverName, databaseName, credential.UserName!, credential.Password!);
```

기존 문구의 "Connect에서 사용자명과 암호를 다시 입력하세요"와 "저장한 Windows 계정에서만
복호화됩니다"는 둘 다 성립하지 않게 되므로 통째로 대체한다.

`credential == null`을 Windows 인증으로 간주하는 기존 폴백은 유지한다. 정상 흐름에서는 이 갈래에
닿지 않는다 — `Connect`가 Windows 인증일 때도 `Set`을 부르므로 항목은 항상 존재한다. 남겨 두는
것은 방어다: 앞으로 `Connect`를 거치지 않고 팩터리에 닿는 경로가 생기더라도, 통합 인증으로
한 번 시도해 보는 편이 예외로 죽는 것보다 낫다.

`BuildWindows`·`BuildSql`은 바뀌지 않는다.

### 4.6. `DbvcServices`

`new SqlCredentialStore()` → `new SessionCredentialStore()`, 그리고 생성자에서
`LegacyCredentialFile.DeleteIfPresent()`를 부른다.

**저장소를 하나만 공유해야 한다는 기존 제약이 더 엄격해진다.** 지금까지는 인스턴스가 갈려도
각자 디스크에서 같은 파일을 읽었으므로 최악의 경우 값이 오래된 정도였다. 이제는 갈리는 순간
다른 인스턴스에 인증 정보가 **아예 없다** — ViewModel이 Connect에서 넣은 암호를 `StateTracker`가
보지 못하면 SQL 인증 접속이 Windows 인증으로 흘러가 실패한다. 생성자 주석을 이 내용으로 고친다.

## 5. Error Handling

| 상황 | 결과 |
| --- | --- |
| 개체 탐색기 선택 없음 / 다중 선택 / 서버 노드 | Connect가 접속하지 않고 경고에 선택 안내. 이미 접속해 둔 대상과 변경 목록은 그대로 남는다 |
| SSMS 어셈블리를 못 찾음 / 리플렉션 예외 | 위와 같다. 사유는 `SsmsDiagnostics`에만 남는다 |
| Entra ID 연결 | 대상 표시만 갱신하고 경고에 사유. 접속하지 않는다 |
| SQL 인증인데 개체 탐색기가 암호를 안 들고 있음 | 암호 없이 저장 → `SqlConnectionFactory`가 `SqlCredentialException`(4.5의 새 문구) |
| 접속 실패 (로그인 실패·네트워크 등) | 기존과 같다. `TestConnection`의 한국어 사유가 경고 배너에 뜬다 |
| 옛 `credentials.json` 삭제 실패 | 삼킨다. `Debug.WriteLine`만 남기고 정상 동작한다 |
| 도구 창이 보이는 채로 개체 탐색기 선택만 바뀜 | 대상은 그대로. 다음에 패널로 시선이 올 때 안내만 뜬다 (4.3.5) |

## 6. Testing Strategy

### 삭제

* `SqlCredentialStoreTests.cs` — 대상 클래스가 사라진다
* `SessionPasswordCacheTests.cs` — 새 저장소 테스트로 흡수

### 신규

* `SessionCredentialStoreTests.cs`
  * `Set` → `TryGet` 왕복, 대소문자 무시 키, 모르는 대상에 `null`을 돌려주는지
  * `Set`이 네 값을 통째로 덮어쓰는지 (이전 암호가 남지 않는지)
  * **저장소를 사용한 뒤 `credentials.json`이 생기지 않는지** — 파일 시스템을 직접 확인한다.
    이것이 이번 결정의 핵심 계약이고, 예전 계약("세션 암호가 파일 내용에 나타나지 않는지")보다
    강하다
* `LegacyCredentialFileTests.cs`
  * 파일이 있으면 지우는지, 없으면 조용한지, 디렉터리를 지우지 않는지, 삭제 실패를 삼키는지

### 수정

* `SqlConnectionFactoryTests.cs` — 메모리 저장소 기준으로 재작성. DPAPI 불가 환경 분기 삭제.
  암호 없는 SQL 인증이 `SqlCredentialException`을 던지는지는 그대로 유지
* `ViewChangesViewModelTests.cs` — 암호 출처 추적·채움 순서 계약·입력란 상호작용 테스트를
  통째로 걷어내고 아래로 대체 (가짜 `ISsmsConnectionSource` 사용)
  * `Connect`: 소스가 `null`을 주면 접속하지 않고 경고만 남는지, **이미 접속해 둔 대상과 변경
    목록이 그대로 남는지**
  * `Connect`: `UnsupportedReason`이 있으면 대상만 갱신하고 `TestConnection`을 부르지 않는지
  * `Connect`: 정상 경로에서 저장소에 네 값이 들어가고 `ApplyContext`가 도는지
  * `Connect`: 같은 대상으로 다시 눌러도 목록이 다시 판정되는지
  * `CheckSsmsSelection`: 4.3.5 표의 다섯 갈래
  * `TargetSummary`: 접속 전 / Windows 인증 / SQL 인증 세 형태
* `PackageTests.cs` — 저장소 타입 참조 갱신

### 단위 테스트로 덮이지 않는 것

`ObjectExplorerConnectionSource`의 리플렉션은 SSMS 프로세스 밖에서 검증할 수 없다. 기존 설계의
판단대로 그 클래스에 로직을 두지 않는 원칙을 유지하고, 아래 수동 절차가 검증을 담당한다.

### 수동 검증 (SSMS 21)

1. 개체 탐색기에서 SQL 인증으로 접속하고 데이터베이스 노드를 선택 → DBVC 창에서 `Connect` →
   대상 표시줄에 `서버.DB — SQL 인증 (계정)`이 뜨고 변경 목록이 채워진다
2. `%APPDATA%\DBVC`에 `credentials.json`이 **없는지** 확인 (기존 파일이 있었다면 지워졌는지)
3. Windows 인증 연결에서도 같은 흐름이 도는지
4. SSMS를 재시작하고 개체 탐색기에 접속하지 않은 채 `Connect` → 선택 안내가 뜨고 접속하지 않는지
5. 개체 탐색기에서 아무것도 선택하지 않거나 서버 노드를 선택한 채 `Connect` → 선택 안내
6. 도구 창을 개체 탐색기와 나란히 띄운 채 다른 DB를 선택 → 패널에 마우스를 올리면 안내가 뜨고,
   `Connect`를 누르면 그 대상으로 전환되는지
7. Entra ID로 접속한 서버를 선택한 채 `Connect` → 사유가 뜨고 접속 시도가 없는지 (가능한 경우)

## 7. 기존 코드에 미치는 영향

* `StateTracker` · `SmoManager` · `GitManager` · `WorkingTreeCleaner` · `ScriptExporter`는
  **바뀌지 않는다.** 자격증명은 인터페이스 뒤에 있다.
* `SqlConnectionFactory`는 `ResolvePassword` 호출이 속성 읽기로 바뀌고 예외 문구가 바뀐다.
* `SqlCredentialException`은 그대로 남는다.
* `DBVC.Core.csproj`에서 DPAPI(`System.Security.Cryptography.ProtectedData`) 참조가 필요 없어지면
  제거한다. `.vsix`에 함께 넣던 런타임 의존이 있으면 그것도 함께 정리한다.
* `ViewChangesViewModel`은 연결 관련 코드가 절반 이하로 줄어든다. Refresh·Commit·Pull·스크립트
  생성 경로는 바뀌지 않는다.

## 8. 문서 갱신

* **README 62행** — "Connect 패널에서 (서버, 데이터베이스)마다 방식을 고를 수 있어… DPAPI로
  암호화해 `credentials.json`에 저장" 전체를, 연결 정보가 개체 탐색기에서만 오고 디스크에
  저장되지 않는다는 서술로 대체한다. Windows 인증과 SQL 인증을 모두 지원한다는 사실은 유지한다 —
  없어진 것은 고르는 방법이지 지원 자체가 아니다.
* **README 64행** — 입력란 채움·마스킹·가져오기 버튼 서술을 `Connect` 한 번의 흐름으로 재작성한다.
* **`docs/setup-checklist.md`** — 183~186행(암호 저장 안내), 201~204행(`credentials.json` 확인),
  291~298행(인증 검증 항목), 346~349행(DPAPI가 Windows 계정에 묶인다는 설명),
  372~373행(문제 해결 표)을 새 동작에 맞게 고친다. 35~37행(혼합 모드 요구)은 그대로 유효하다.
* **기존 설계 문서들은 수정하지 않는다.** 그때의 결정 기록이고, 이 문서가 그것을 대체한다는
  사실은 1절에 적혀 있다.

## 9. 잃는 것

개체 탐색기에서 연결을 읽지 못하면 DBVC로 할 수 있는 일이 없다. 우회 입력이 사라지므로,
리플렉션 경로가 SSMS 22에서 깨지면 **자동 채움만 멈추는 것이 아니라 플러그인 전체가 멈춘다.**

이것이 이번 단순화의 대가다. 그래서 두 가지가 이전보다 중요해진다.

* `SsmsDiagnostics` 로그가 유일한 실패 원인 추적 수단이다. 읽기 실패의 사유는 어댑터가,
  채택한 대상은 4.3.3의 `Connect()`가 남기므로 두 로그를 이어 붙이면 어느 관문에서 멈췄는지
  드러난다.
* 4.3.5의 안내 문구가 "무엇을 해야 하는가"를 말하는 유일한 곳이다. 개체 탐색기를 쓸 줄 모르는
  사용자에게 이 한 줄 말고는 길잡이가 없다.
