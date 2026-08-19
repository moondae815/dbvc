# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 빌드 · 테스트

```bash
dotnet build DBVC.slnx                       # 전체 (net48 + netstandard2.0 + net10.0 테스트)
dotnet test tests/DBVC.Core.Tests
dotnet test tests/DBVC.Vsix.Tests

# 단일 테스트 / 픽스처
dotnet test tests/DBVC.Core.Tests --filter "FullyQualifiedName~GetStatus_ReturnsClean"

# 프레임워크 지정 (테스트 프로젝트는 net48;net10.0 멀티타깃)
dotnet test tests/DBVC.Core.Tests -f net48    # Windows에서만 실행 가능
dotnet test tests/DBVC.Core.Tests -f net10.0
```

`.vsix` 패키징은 `dotnet build`로도 된다. 이 저장소의 현재 구성에서 확인했다 — 이렇게 만든
Release 패키지는 msbuild 산출물과 담긴 항목 85개가 같고, 매니페스트의 `InstallationTarget`도
그대로 들어간다:

```powershell
dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release
dir src\DBVC.Vsix\bin\Release\net48\*.vsix   # 빌드 성공 ≠ .vsix 생성. 산출물 존재를 반드시 확인한다.
```

제약은 빌드 도구가 아니라 `Microsoft.VSSDK.BuildTools`가 복원·임포트되느냐다. 그것이 안 되면
msbuild로도 `.vsix`는 나오지 않는다 — GitHub Actions의 `windows-latest`가 그런 경우여서 VSIX
패키징은 CI에서 제외되어 있다(README의 "알려진 이슈"). 개발자 셸에서
`msbuild src/DBVC.Vsix/DBVC.Vsix.csproj -restore -p:Configuration=Release`도 그대로 동작하므로,
`dotnet build`가 산출물을 내지 않으면 그쪽으로 한 번 더 확인해 본다.

`SmoManagerIntegrationTests`는 `localhost`의 SQL Server에 Windows 인증으로 붙어 임시 DB를 만든다.
접속되지 않으면 실패가 아니라 Skip이다 — 이 경로를 실제로 고칠 때는 로컬 SQL Server가 필요하다.

## 아키텍처

두 계층이다. `DBVC.Core`(netstandard2.0 + net48)에 모든 로직이 있고, `DBVC.Vsix`(net48, WPF/MVVM)는
SSMS 21(VS 2022 셸) 안에서 그것을 띄운다. Core는 VS 셸을 전혀 모르므로 셸 없이 테스트된다.

**변경이 흐르는 경로**

1. `src/DBVC.Database/InstallTrigger.sql`(Core에 임베디드 리소스)이 대상 DB에 `DBVC_ChangeLog`
   테이블과 DATABASE DDL 트리거 `trg_DBVC_DDL_Tracker`를 설치한다 — 멱등이다.
2. `StateTracker`가 `IsProcessed = 0` 레코드를 읽어 바뀐 객체 이름을 낸다. 폴링은 없다 —
   새로고침·연결·초기화·커밋 뒤에만 갱신된다.
3. `SmoManager`가 **그 객체만** SMO로 스크립팅해 저장소에 쓴다. 전체 추출은 기준선이 없을 때
   (`ExtractionBaseline.Exists`가 false) 자동으로, 아니면 "전체 다시 추출"로만 일어난다.
4. `WorkingTreeCleaner`가 DROP된 객체의 `.sql`을 지우고, `GitManager`(LibGit2Sharp)가 스테이징·
   커밋·Pull·Push·이력 조회를 한다. 커밋에 성공하면 `StateTracker.MarkProcessed`로 로그를 닫는다.

**핵심 규약**

- 저장소 경로는 `ObjectPathConvention` 한 곳에서만 정한다: `[Schema]/[ObjectType]/[Name].sql`,
  구분자는 항상 `/`. SMO 타입명과 DDL 트리거 EVENTDATA의 ObjectType을 같은 폴더로 매핑하며,
  역파싱(`TryParseRelativePath`)과 배포 스크립트 정렬 순서도 여기에 있다.
- DB↔저장소 매핑은 `%APPDATA%\DBVC\mappings.json`(`ConfigManager`). 모든 Core API가
  `(serverName, databaseName)`를 받아 이 매핑으로 저장소 경로를 찾는다.
- **인증 정보는 디스크에 쓰지 않는다.** `SessionCredentialStore`는 메모리 전용이고, 값의 유일한
  출처는 SSMS 개체 탐색기(`ObjectExplorerConnectionSource`)다. DBVC 창에 입력란은 없다.
- Git 인증은 **SSH만** 지원한다. libgit2가 시스템 `ssh`에 위임하므로 자격증명 콜백을 타지 않는다.
  HTTPS 원격은 `RemoteDiagnostics`가 사유를 만들어 안내한다.
- `Abstractions.cs`의 인터페이스(`IConfigManager`, `IStateTracker`, `IGitManager`, `ISmoManager`,
  `ISqlCredentialStore`, `IWorkingTreeCleaner`)가 UI를 DB·Git 없이 테스트 가능하게 하는 이음매다.
  `DbvcServices`가 조립 루트이며, 하나의 `ConfigManager`와 자격증명 저장소를 모든 매니저가 공유한다
  (따로 만들면 SQL 인증 암호가 매니저에 전달되지 않는다).
- 무거운 작업은 `IBackgroundScheduler`로 UI 스레드 밖에서 돈다. 인라인으로 되돌리면 새로고침이
  다시 SSMS를 붙잡는다. 반대로 `ObjectExplorerConnectionSource`는 **UI 스레드에서만** 부른다.
- SSMS 어셈블리는 컴파일 타임에 참조하지 않는다 — 리플렉션으로 읽는다(설치 폴더에만 있고 GAC에
  없다). 판단 로직은 테스트 가능한 `SsmsUrn`으로 빼고, 어댑터는 속성 읽기와 얇은 분기만 남긴다.

## 절대 건드리지 말 것

- **패키지 버전 고정.** `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects
  171.30.0`은 SSMS 21이 프로세스에 먼저 올리는 어셈블리에 맞춘 값이다. 올리면 SSMS가 제공하지 않는
  `System.Diagnostics.DiagnosticSource`를 요구하게 되어 `SqlConnection` 형식 이니셜라이저가 죽고,
  인증 방식과 무관하게 어떤 DB에도 접속되지 않는다. 근거는 `DBVC.Core.csproj` 주석에 있다.
- **테스트 프로젝트에 MDS/SMO를 직접 PackageReference 하지 않는다.** 그쪽이 이기면 테스트 호스트가
  Core와 다른 SMO를 올려 `TypeLoadException`이 나고, `SmoManager`가 그 예외를 삼켜 조용히 통과한다.
  전이 참조로만 받는다.
- `DBVC.Vsix.csproj`의 `RegisterWithCodebase`, `$(VSToolsPath)\VSSDK\Microsoft.VsSDK.targets`
  명시적 Import, `IncludeCoreDependenciesInVsix` 목록, 매니페스트의
  `InstallationTarget Microsoft.VisualStudio.Ssms [21.0, 22.0)` — 넷 다 빼면 **빌드와 설치가 성공한
  뒤 런타임에 조용히** 깨진다(메뉴 미등록, 저장 실패, SSMS 아닌 VS에 설치됨). 각 자리의 주석에
  실제 증상이 기록되어 있으니 바꾸기 전에 읽는다.
- 참조가 바뀌면 `IncludeCoreDependenciesInVsix` 목록을 `DBVC.Core.dll`의 AssemblyRef 폐포에서 다시
  계산한다. 예외 메시지를 보고 하나씩 이름을 보태는 방식은 쓰지 않는다.

## 작업 방식

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 알림, 버튼, ToolTip, 컬럼명 포함.
  libgit2/서버의 영문 원문은 응답을 인용할 때만 그대로 싣는다. Core는 상태를 영어 식별자로 다루고
  화면 계층에서만 한국어로 옮긴다.
- 주석은 **"왜"만** 적는다. 한국어 평서문으로, 함정과 근거를 남기는 기존 문체를 따른다.
- 커밋 메시지는 한국어 명령형 현재시제 + 스코프: `feat(core): 메모리 전용 자격증명 저장소를 더한다`.
- TDD: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋. 테스트 이름은 영어
  `Method_Result_WhenCondition` 형태다.
- 기능 작업은 `docs/superpowers/specs/`(설계) → `docs/superpowers/plans/`(구현 계획, 체크박스) →
  구현 순서로 진행한다. 새 작업을 시작하기 전에 관련 문서를 먼저 읽는다.
- 사용자 눈에 보이는 동작이 바뀌면 `README.md`와 `docs/setup-checklist.md`를 함께 고치고,
  `src/DBVC.Vsix/source.extension.vsixmanifest`의 버전을 올린다.
- **CI가 검증하지 않는 것:** WPF 렌더링, VS 패키지 로딩, `.vsct` 메뉴 등록, SSMS 통합, 실제 DB 연결.
  이 영역을 건드렸다면 SSMS 21에서 직접 눌러 보기 전에는 "동작한다"고 말할 수 없다.
- 미구현 기능은 Object Explorer 아이콘 오버레이(Feature 10) 하나이며, 공개 확장점이 없어 보류된
  상태다. 사유는 `docs/superpowers/plans/2026-08-01-dbvc-object-explorer-overlay.md`에 있다.
