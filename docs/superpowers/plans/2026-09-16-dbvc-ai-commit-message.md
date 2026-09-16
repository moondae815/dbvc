# AI 커밋 메시지 생성 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DBVC의 커밋 메시지 칸 옆에 `AI 생성` 버튼을 두어, 선택한 변경의 unified diff를 OpenAI 호환 엔드포인트에 보내 형식에 맞는 한국어 커밋 메시지 초안을 받아 채운다.

**Architecture:** 로직은 전부 `DBVC.Core`에 둔다 — 설정 저장(`AiSettingsStore`, DPAPI), HTTP 호출(`OpenAiCompatibleClient`), 프롬프트 조립·응답 정제(`CommitMessageComposer`), diff 추출(`GitManager.GetUnifiedDiff`). `DBVC.Vsix`는 버튼 하나와 옵션 페이지 하나만 더한다. 커밋 경로는 바뀌지 않는다 — AI는 TextBox를 채울 뿐이다.

**Tech Stack:** .NET (net48 + netstandard2.0 / 테스트는 net48 + net10.0), NUnit 4, LibGit2Sharp 0.32.0, `System.Text.Json` 10.0.3, `System.Net.Http`, WPF, VS SDK(`UIElementDialogPage`).

**Spec:** [`docs/superpowers/specs/2026-09-16-dbvc-ai-commit-message-design.md`](../specs/2026-09-16-dbvc-ai-commit-message-design.md)

## Global Constraints

이 절의 제약은 모든 태스크의 요구사항에 암묵적으로 포함된다.

- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0`은 SSMS 21에 맞춘 고정값이다. 이 계획이 추가하는 패키지는 **단 하나**, `System.Security.Cryptography.ProtectedData`이며 **netstandard2.0 타깃에만** 건다.
- **테스트 프로젝트에 MDS/SMO를 직접 PackageReference 하지 않는다.** 전이 참조로만 받는다.
- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지·버튼·ToolTip·안내 포함. 서버가 돌려준 영문 원문은 인용할 때만 그대로 싣는다.
- **주석은 "왜"만 적는다.** 한국어 평서문, 기존 문체를 따른다.
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프**: `feat(core): AI 설정 저장소를 더한다`. 끝에 다음 줄을 붙인다:
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- **테스트 이름은 영어 `Method_Result_WhenCondition` 형태다.**
- **TDD**: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋.
- 생성되는 커밋 메시지 형식(AI 산출물): **한국어 본문 + 영문 접두어, 스코프 없음**, 접두어는 `feat|fix|refactor|perf|chore` 중 하나, 72자 이내, 마침표 없음.
- 기본값: `TimeoutSeconds = 30`, `MaxDiffLines = 400`.
- 설정 파일 경로: `%APPDATA%\DBVC\ai-settings.json` (`mappings.json`과 **별도 파일**).

**테스트 실행 명령** (Windows 개발 PC 기준):

```bash
dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~<TestName>"
dotnet test tests/DBVC.Core.Tests -f net10.0      # Core 전체
dotnet test tests/DBVC.Vsix.Tests -f net48        # Vsix는 net48에서
```

---

## File Structure

| 파일 | 책임 |
| --- | --- |
| `src/DBVC.Core/Models/AiSettings.cs` | 설정 데이터(순수) |
| `src/DBVC.Core/SecretProtector.cs` | `ISecretProtector` + `DpapiSecretProtector` |
| `src/DBVC.Core/AiSettingsStore.cs` | `ai-settings.json` 읽기·쓰기, 키 보호 |
| `src/DBVC.Core/Models/DiffFileChange.cs` | 파일 하나의 상태 + patch 본문 |
| `src/DBVC.Core/CommitMessageComposer.cs` | 프롬프트 조립, diff 잘라내기, 응답 정제(정적·순수) |
| `src/DBVC.Core/OpenAiCompatibleClient.cs` | `IChatCompletionClient` + HTTP 구현 + `AiRequestException` |
| `src/DBVC.Core/AiConsent.cs` | 호스트 추출과 동의 필요 여부 판정(정적·순수) |
| `src/DBVC.Core/CommitMessageGenerator.cs` | 위의 것들을 엮는 조립자 |
| `src/DBVC.Core/GitManager.cs` | `GetUnifiedDiff` 추가 |
| `src/DBVC.Core/Abstractions.cs` | `IAiCommitMessageGenerator`, `IGitManager.GetUnifiedDiff` 추가 |
| `src/DBVC.Vsix/UI/AiOptionsControl.xaml(.cs)` | 옵션 화면(WPF) |
| `src/DBVC.Vsix/UI/AiOptionPage.cs` | `UIElementDialogPage` |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | `GenerateCommitMessageCommand` 추가 |

---

## Task 1: 비밀 보호 이음매

**Files:**
- Create: `src/DBVC.Core/SecretProtector.cs`
- Modify: `src/DBVC.Core/DBVC.Core.csproj`
- Test: `tests/DBVC.Core.Tests/SecretProtectorTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `ISecretProtector.Protect(string) → string`, `ISecretProtector.Unprotect(string) → string?`, `DpapiSecretProtector`(기본 구현)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SecretProtectorTests
    {
        [Test]
        public void Unprotect_ReturnsOriginal_WhenRoundTripped()
        {
            var protector = new DpapiSecretProtector();

            var protectedText = protector.Protect("sk-test-1234");

            Assert.That(protector.Unprotect(protectedText), Is.EqualTo("sk-test-1234"));
        }

        [Test]
        public void Protect_DoesNotContainPlainText_WhenProtected()
        {
            var protector = new DpapiSecretProtector();

            var protectedText = protector.Protect("sk-test-1234");

            // 이 단언이 이 클래스의 존재 이유다. 통과하지 않으면 뒤의 모든 저장 로직이 무의미하다.
            Assert.That(protectedText, Does.Not.Contain("sk-test-1234"));
        }

        [Test]
        public void Unprotect_ReturnsNull_WhenTextIsNotProtectedData()
        {
            var protector = new DpapiSecretProtector();

            // 사용자가 파일을 손으로 고쳤거나 다른 계정이 암호화한 값이다.
            // 예외가 아니라 null이어야 화면이 "키를 다시 입력하세요"로 흘러간다.
            Assert.That(protector.Unprotect("not-protected-data"), Is.Null);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SecretProtectorTests"`
Expected: FAIL — `DpapiSecretProtector` 형식을 찾을 수 없다는 컴파일 오류.

- [ ] **Step 3: csproj에 타깃별 참조를 더한다**

`src/DBVC.Core/DBVC.Core.csproj`의 기존 `<ItemGroup>` 아래에 다음 두 덩어리를 더한다.

```xml
  <!--
    ProtectedData(DPAPI)는 net48에서는 프레임워크의 System.Security에 있지만
    netstandard2.0에는 없다. 타깃마다 출처가 달라 조건부로 건다.
    net48 쪽 참조는 늘지 않으므로 DBVC.Vsix의 IncludeCoreDependenciesInVsix 목록은
    다시 계산할 필요가 없다 — 이 조건을 지우면 그때는 재계산 대상이 된다.
  -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <Reference Include="System.Security" />
  </ItemGroup>

  <ItemGroup Condition="'$(TargetFramework)' == 'netstandard2.0'">
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />
  </ItemGroup>
```

- [ ] **Step 4: 최소 구현을 쓴다**

`src/DBVC.Core/SecretProtector.cs`:

```csharp
using System;
using System.Security.Cryptography;
using System.Text;

namespace DBVC.Core
{
    /// <summary>
    /// 디스크에 남겨야 하는 비밀을 보호한다.
    ///
    /// 이음매로 둔 이유는 테스트다 — 이것이 없으면 설정 저장소를 검증할 때마다
    /// 실행 계정의 진짜 DPAPI 키를 타게 되고, 실패했을 때 저장 로직이 틀린 것인지
    /// 암호화가 틀린 것인지 구분되지 않는다.
    /// </summary>
    public interface ISecretProtector
    {
        string Protect(string plainText);

        /// <summary>복원할 수 없으면 <c>null</c>이다. 다른 계정이 암호화했거나 파일이 손상된 경우다.</summary>
        string? Unprotect(string protectedText);
    }

    /// <summary>
    /// Windows DPAPI(CurrentUser)로 보호한다. 같은 PC의 다른 계정은 복호화하지 못한다.
    /// </summary>
    public sealed class DpapiSecretProtector : ISecretProtector
    {
        public string Protect(string plainText)
        {
            if (plainText == null) throw new ArgumentNullException(nameof(plainText));

            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public string? Unprotect(string protectedText)
        {
            if (string.IsNullOrWhiteSpace(protectedText)) return null;

            try
            {
                var protectedBytes = Convert.FromBase64String(protectedText);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                // Base64가 아니다 — 손으로 고친 파일이다.
                return null;
            }
            catch (CryptographicException)
            {
                // 다른 계정이 암호화했거나 값이 깨졌다.
                return null;
            }
        }
    }
}
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SecretProtectorTests"`
Expected: PASS (3개)

- [ ] **Step 6: net48에서도 컴파일되는지 확인한다**

Run: `dotnet build src/DBVC.Core/DBVC.Core.csproj`
Expected: net48·netstandard2.0 둘 다 성공.

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Core/SecretProtector.cs src/DBVC.Core/DBVC.Core.csproj tests/DBVC.Core.Tests/SecretProtectorTests.cs
git commit -m "feat(core): DPAPI 비밀 보호 이음매를 더한다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: AI 설정 모델과 저장소

**Files:**
- Create: `src/DBVC.Core/Models/AiSettings.cs`, `src/DBVC.Core/AiSettingsStore.cs`
- Test: `tests/DBVC.Core.Tests/AiSettingsStoreTests.cs`

**Interfaces:**
- Consumes: `ISecretProtector`(Task 1)
- Produces:
  - `AiSettings { string BaseUrl; string Model; string ApiKey; int TimeoutSeconds; int MaxDiffLines; string? ConsentedHost; bool IsConfigured }`
  - `IAiSettingsStore { AiSettings Load(); void Save(AiSettings settings); }`
  - `AiSettingsStore(string filePath, ISecretProtector protector)`, `AiSettingsStore.DefaultFilePath`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System;
using System.IO;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class AiSettingsStoreTests
    {
        private string _path = string.Empty;

        /// <summary>
        /// 보호를 흉내만 낸다. 진짜 DPAPI를 타면 이 픽스처가 검증하는 것이
        /// 저장 로직인지 암호화인지 구분되지 않는다.
        /// </summary>
        private sealed class ReversibleProtector : ISecretProtector
        {
            public string Protect(string plainText) =>
                "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));

            public string? Unprotect(string protectedText) =>
                protectedText.StartsWith("enc:", StringComparison.Ordinal)
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText.Substring(4)))
                    : null;
        }

        [SetUp]
        public void CreateTempPath()
        {
            _path = Path.Combine(Path.GetTempPath(), "dbvc_ai_" + Guid.NewGuid().ToString("N"), "ai-settings.json");
        }

        [TearDown]
        public void DeleteTempPath()
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void Load_ReturnsSavedValues_WhenRoundTripped()
        {
            var store = new AiSettingsStore(_path, new ReversibleProtector());
            store.Save(new AiSettings
            {
                BaseUrl = "https://llm.example.com/v1",
                Model = "gpt-4o-mini",
                ApiKey = "sk-secret-value",
                TimeoutSeconds = 45,
                MaxDiffLines = 200,
                ConsentedHost = "llm.example.com",
            });

            var loaded = new AiSettingsStore(_path, new ReversibleProtector()).Load();

            Assert.Multiple(() =>
            {
                Assert.That(loaded.BaseUrl, Is.EqualTo("https://llm.example.com/v1"));
                Assert.That(loaded.Model, Is.EqualTo("gpt-4o-mini"));
                Assert.That(loaded.ApiKey, Is.EqualTo("sk-secret-value"));
                Assert.That(loaded.TimeoutSeconds, Is.EqualTo(45));
                Assert.That(loaded.MaxDiffLines, Is.EqualTo(200));
                Assert.That(loaded.ConsentedHost, Is.EqualTo("llm.example.com"));
            });
        }

        [Test]
        public void Save_WritesNoPlainTextKey_WhenApiKeyGiven()
        {
            var store = new AiSettingsStore(_path, new ReversibleProtector());

            store.Save(new AiSettings { BaseUrl = "https://x/v1", Model = "m", ApiKey = "sk-secret-value" });

            // 이 기능에서 가장 값진 단언이다. 한번 깨지면 조용히 깨진다.
            Assert.That(File.ReadAllText(_path), Does.Not.Contain("sk-secret-value"));
        }

        [Test]
        public void Load_ReturnsDefaults_WhenFileMissing()
        {
            var loaded = new AiSettingsStore(_path, new ReversibleProtector()).Load();

            Assert.Multiple(() =>
            {
                Assert.That(loaded.BaseUrl, Is.Empty);
                Assert.That(loaded.TimeoutSeconds, Is.EqualTo(30));
                Assert.That(loaded.MaxDiffLines, Is.EqualTo(400));
                Assert.That(loaded.IsConfigured, Is.False);
            });
        }

        [Test]
        public void Load_ReturnsEmptyKey_WhenProtectedValueCannotBeRead()
        {
            // 다른 계정이 암호화한 파일을 받은 경우다. 예외로 죽으면 화면 전체가 열리지 않는다.
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{\"baseUrl\":\"https://x/v1\",\"model\":\"m\",\"protectedApiKey\":\"garbage\"}");

            var loaded = new AiSettingsStore(_path, new ReversibleProtector()).Load();

            Assert.Multiple(() =>
            {
                Assert.That(loaded.ApiKey, Is.Empty);
                Assert.That(loaded.BaseUrl, Is.EqualTo("https://x/v1"));
            });
        }

        [Test]
        public void IsConfigured_ReturnsTrue_WhenBaseUrlAndModelPresent()
        {
            // 키는 필수가 아니다 — 사내 LLM 서버는 인증 없이 열려 있는 경우가 있다.
            var settings = new AiSettings { BaseUrl = "https://x/v1", Model = "m" };

            Assert.That(settings.IsConfigured, Is.True);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~AiSettingsStoreTests"`
Expected: FAIL — `AiSettings`/`AiSettingsStore` 형식을 찾을 수 없다.

- [ ] **Step 3: 모델을 쓴다**

`src/DBVC.Core/Models/AiSettings.cs`:

```csharp
namespace DBVC.Core.Models
{
    /// <summary>
    /// AI 커밋 메시지 생성에 필요한 설정. 디스크 표현은 <see cref="DBVC.Core.AiSettingsStore"/>가 정한다.
    /// </summary>
    public class AiSettings
    {
        /// <summary>OpenAI 호환 엔드포인트의 기준 URL. 예: <c>https://api.openai.com/v1</c></summary>
        public string BaseUrl { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        /// <summary>평문. 디스크에는 보호된 형태로만 닿는다.</summary>
        public string ApiKey { get; set; } = string.Empty;

        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>AI에게 보내는 diff의 최대 줄 수. 토큰 비용이 아니라 전송량 자체의 한계다.</summary>
        public int MaxDiffLines { get; set; } = 400;

        /// <summary>
        /// 사용자가 전송에 동의한 호스트. 값이 다르면 다시 묻는다 —
        /// 동의는 "AI를 쓰는 것"이 아니라 "이 목적지로 보내는 것"에 대한 것이다.
        /// </summary>
        public string? ConsentedHost { get; set; }

        /// <summary>
        /// 호출을 시도해 볼 수 있는 상태인지. API 키는 조건이 아니다 —
        /// 사내 LLM 서버는 인증 없이 열려 있는 경우가 있고, 그때 키를 요구하면 쓸 수 없다.
        /// </summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
    }
}
```

- [ ] **Step 4: 저장소를 쓴다**

`src/DBVC.Core/AiSettingsStore.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBVC.Core.Models;

namespace DBVC.Core
{
    public interface IAiSettingsStore
    {
        AiSettings Load();
        void Save(AiSettings settings);
    }

    /// <summary>
    /// AI 설정을 <c>%APPDATA%\DBVC\ai-settings.json</c>에 둔다.
    ///
    /// <c>mappings.json</c>과 다른 파일인 것이 중요하다. 매핑은 (서버, DB)마다 다르고
    /// 팀원끼리 내용을 주고받는 일이 실제로 있다 — 같은 파일에 API 키가 들어 있으면 그때 딸려 나간다.
    /// </summary>
    public class AiSettingsStore : IAiSettingsStore
    {
        private readonly string _filePath;
        private readonly ISecretProtector _protector;
        private readonly object _fileLock = new object();

        public static string DefaultFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBVC",
            "ai-settings.json");

        public AiSettingsStore() : this(DefaultFilePath, new DpapiSecretProtector())
        {
        }

        public AiSettingsStore(string filePath, ISecretProtector protector)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty.", nameof(filePath));
            _filePath = filePath;
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        }

        public string FilePath => _filePath;

        public AiSettings Load()
        {
            lock (_fileLock)
            {
                if (!File.Exists(_filePath)) return new AiSettings();

                try
                {
                    var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(_filePath));
                    if (payload == null) return new AiSettings();

                    return new AiSettings
                    {
                        BaseUrl = payload.BaseUrl ?? string.Empty,
                        Model = payload.Model ?? string.Empty,
                        // 복원하지 못해도 나머지 설정은 살린다. 키만 다시 넣으면 되는 상황에서
                        // 전부 기본값으로 돌아가면 사용자는 무엇이 사라졌는지 알 수 없다.
                        ApiKey = string.IsNullOrEmpty(payload.ProtectedApiKey)
                            ? string.Empty
                            : _protector.Unprotect(payload.ProtectedApiKey!) ?? string.Empty,
                        TimeoutSeconds = payload.TimeoutSeconds > 0 ? payload.TimeoutSeconds : 30,
                        MaxDiffLines = payload.MaxDiffLines > 0 ? payload.MaxDiffLines : 400,
                        ConsentedHost = payload.ConsentedHost,
                    };
                }
                catch (JsonException)
                {
                    // 손상된 파일로 화면 전체가 열리지 않는 일은 없어야 한다.
                    return new AiSettings();
                }
                catch (IOException)
                {
                    return new AiSettings();
                }
            }
        }

        public void Save(AiSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var payload = new Payload
            {
                BaseUrl = settings.BaseUrl,
                Model = settings.Model,
                ProtectedApiKey = string.IsNullOrEmpty(settings.ApiKey)
                    ? null
                    : _protector.Protect(settings.ApiKey),
                TimeoutSeconds = settings.TimeoutSeconds,
                MaxDiffLines = settings.MaxDiffLines,
                ConsentedHost = settings.ConsentedHost,
            };

            lock (_fileLock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

                File.WriteAllText(
                    _filePath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        /// <summary>디스크 표현. 키 항목의 이름을 <c>protectedApiKey</c>로 둔 것은 의도다 —
        /// 파일을 연 사람이 평문을 기대하지 않게 한다.</summary>
        private sealed class Payload
        {
            [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
            [JsonPropertyName("model")] public string? Model { get; set; }
            [JsonPropertyName("protectedApiKey")] public string? ProtectedApiKey { get; set; }
            [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; }
            [JsonPropertyName("maxDiffLines")] public int MaxDiffLines { get; set; }
            [JsonPropertyName("consentedHost")] public string? ConsentedHost { get; set; }
        }
    }
}
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~AiSettingsStoreTests"`
Expected: PASS (5개)

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Core/Models/AiSettings.cs src/DBVC.Core/AiSettingsStore.cs tests/DBVC.Core.Tests/AiSettingsStoreTests.cs
git commit -m "feat(core): AI 설정을 별도 파일에 암호화해 보관한다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: 응답 정제

**Files:**
- Create: `src/DBVC.Core/CommitMessageComposer.cs`
- Test: `tests/DBVC.Core.Tests/CommitMessageComposerTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `CommitMessageComposer.Clean(string? raw) → string`, `CommitMessageComposer.AllowedPrefixes`(읽기 전용 배열)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class CommitMessageComposerCleanTests
    {
        [Test]
        public void Clean_ReturnsMessage_WhenAlreadyWellFormed()
        {
            Assert.That(
                CommitMessageComposer.Clean("feat: 주문 조회 프로시저에 취소일자 필터를 더한다"),
                Is.EqualTo("feat: 주문 조회 프로시저에 취소일자 필터를 더한다"));
        }

        [Test]
        public void Clean_RemovesCodeFence_WhenResponseIsWrapped()
        {
            var raw = "```\nfix: 재고 계산의 반올림 오류를 고친다\n```";

            Assert.That(CommitMessageComposer.Clean(raw), Is.EqualTo("fix: 재고 계산의 반올림 오류를 고친다"));
        }

        [Test]
        public void Clean_KeepsFirstLineOnly_WhenResponseHasBody()
        {
            var raw = "feat: 회원 등급 뷰를 더한다\n\n- 등급 산출 기준을 바꾼다\n- 인덱스를 더한다";

            Assert.That(CommitMessageComposer.Clean(raw), Is.EqualTo("feat: 회원 등급 뷰를 더한다"));
        }

        [Test]
        public void Clean_RemovesSurroundingQuotes_WhenResponseIsQuoted()
        {
            Assert.That(
                CommitMessageComposer.Clean("\"chore: 사용하지 않는 프로시저를 지운다\""),
                Is.EqualTo("chore: 사용하지 않는 프로시저를 지운다"));
        }

        [Test]
        public void Clean_AddsChorePrefix_WhenPrefixMissing()
        {
            // 형식 위반은 드물어도 0이 되지 않는다. 접두어 없는 메시지가 이력에 남지 않게 한다.
            Assert.That(
                CommitMessageComposer.Clean("주문 테이블에 컬럼을 더한다"),
                Is.EqualTo("chore: 주문 테이블에 컬럼을 더한다"));
        }

        [Test]
        public void Clean_AddsChorePrefix_WhenPrefixIsNotAllowed()
        {
            // style은 이 저장소가 쓰는 집합에 없다.
            Assert.That(
                CommitMessageComposer.Clean("style: 들여쓰기를 고친다"),
                Is.EqualTo("chore: style: 들여쓰기를 고친다"));
        }

        [Test]
        public void Clean_KeepsScopedPrefix_WhenModelAddsScope()
        {
            // 스코프를 쓰지 말라고 했는데도 붙는 경우가 있다. 형식으로는 유효하므로 그대로 둔다.
            Assert.That(
                CommitMessageComposer.Clean("feat(dbo): 주문 뷰를 더한다"),
                Is.EqualTo("feat(dbo): 주문 뷰를 더한다"));
        }

        [Test]
        public void Clean_TruncatesTo72Characters_WhenMessageTooLong()
        {
            var raw = "feat: " + new string('가', 100);

            var cleaned = CommitMessageComposer.Clean(raw);

            Assert.That(cleaned.Length, Is.EqualTo(72));
        }

        [Test]
        public void Clean_ReturnsEmpty_WhenResponseIsEmpty()
        {
            // 호출자가 "빈 응답"을 실패로 다룰 수 있어야 한다. 여기서 문장을 지어내지 않는다.
            Assert.That(CommitMessageComposer.Clean("   "), Is.Empty);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CommitMessageComposerCleanTests"`
Expected: FAIL — `CommitMessageComposer` 형식을 찾을 수 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Core/CommitMessageComposer.cs`:

```csharp
using System;
using System.Linq;

namespace DBVC.Core
{
    /// <summary>
    /// AI에게 보낼 프롬프트를 짓고, 돌아온 응답을 커밋 메시지 한 줄로 정리한다.
    ///
    /// 정적·순수로 둔 이유: 이 기능에서 가장 자주 틀리는 자리가 여기인데,
    /// 네트워크도 Git도 끼지 않으면 그 판단만 따로 검증할 수 있다.
    /// </summary>
    public static class CommitMessageComposer
    {
        /// <summary>스키마 저장소에 해당하는 변경만 남긴 집합. docs·test는 여기 없다.</summary>
        public static readonly string[] AllowedPrefixes = { "feat", "fix", "refactor", "perf", "chore" };

        private const int MaxLength = 72;

        /// <summary>
        /// 모델 응답을 커밋 메시지 한 줄로 만든다. 빈 문자열이면 쓸 만한 응답이 없었다는 뜻이다 —
        /// 호출자가 실패로 다룬다.
        /// </summary>
        public static string Clean(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var text = raw!.Replace("\r\n", "\n").Trim();

            // 코드펜스를 씌워 돌려주는 모델이 있다. 여는 줄에 ```sql 같은 언어 표시가 붙기도 한다.
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var lines = text.Split('\n').ToList();
                lines.RemoveAt(0);
                if (lines.Count > 0 && lines[lines.Count - 1].TrimEnd().StartsWith("```", StringComparison.Ordinal))
                {
                    lines.RemoveAt(lines.Count - 1);
                }
                text = string.Join("\n", lines).Trim();
            }

            var firstLine = text.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
            if (string.IsNullOrWhiteSpace(firstLine)) return string.Empty;

            firstLine = TrimWrapper(firstLine!, '"');
            firstLine = TrimWrapper(firstLine, '\'');
            firstLine = TrimWrapper(firstLine, '`');

            if (!HasAllowedPrefix(firstLine))
            {
                firstLine = "chore: " + firstLine;
            }

            if (firstLine.Length > MaxLength)
            {
                firstLine = firstLine.Substring(0, MaxLength).TrimEnd();
            }

            return firstLine;
        }

        private static string TrimWrapper(string value, char wrapper)
        {
            if (value.Length >= 2 && value[0] == wrapper && value[value.Length - 1] == wrapper)
            {
                return value.Substring(1, value.Length - 2).Trim();
            }
            return value;
        }

        /// <summary>
        /// <c>feat:</c> 또는 <c>feat(scope):</c> 형태인지 본다. 스코프는 쓰지 말라고 프롬프트에
        /// 적지만 붙는 경우가 있고, 형식으로는 유효하므로 고치지 않는다.
        /// </summary>
        private static bool HasAllowedPrefix(string message)
        {
            var colon = message.IndexOf(':');
            if (colon <= 0) return false;

            var head = message.Substring(0, colon);
            var paren = head.IndexOf('(');
            if (paren > 0) head = head.Substring(0, paren);

            return AllowedPrefixes.Contains(head.Trim(), StringComparer.OrdinalIgnoreCase);
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CommitMessageComposerCleanTests"`
Expected: PASS (9개)

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/CommitMessageComposer.cs tests/DBVC.Core.Tests/CommitMessageComposerTests.cs
git commit -m "feat(core): 모델 응답을 커밋 메시지 한 줄로 정리한다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: 프롬프트 조립과 diff 잘라내기

**Files:**
- Create: `src/DBVC.Core/Models/DiffFileChange.cs`
- Modify: `src/DBVC.Core/CommitMessageComposer.cs`
- Modify: `tests/DBVC.Core.Tests/CommitMessageComposerTests.cs` (픽스처 추가)

**Interfaces:**
- Consumes: `CommitMessageComposer`(Task 3)
- Produces:
  - `DiffFileChange { string RelativePath; string Status; string Patch; }` — `Status`는 `"Added" | "Modified" | "Deleted"`
  - `CommitMessageComposer.SystemPrompt` (상수)
  - `CommitMessageComposer.BuildUserMessage(IReadOnlyList<DiffFileChange> changes, int maxDiffLines) → string`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/CommitMessageComposerTests.cs`에 픽스처를 더한다.

```csharp
    [TestFixture]
    public class CommitMessageComposerPromptTests
    {
        private static DiffFileChange Change(string path, string status, string patch) =>
            new DiffFileChange { RelativePath = path, Status = status, Patch = patch };

        [Test]
        public void BuildUserMessage_ListsObjectNameAndState_WhenChangeGiven()
        {
            var changes = new[] { Change("dbo/Procedures/usp_GetOrder.sql", "Modified", "@@ -1 +1 @@\n-old\n+new") };

            var message = CommitMessageComposer.BuildUserMessage(changes, maxDiffLines: 400);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("dbo.usp_GetOrder"));
                Assert.That(message, Does.Contain("수정"));
            });
        }

        [Test]
        public void BuildUserMessage_UsesRelativePath_WhenPathIsNotConventional()
        {
            // 규약 밖 경로를 조용히 버리면 AI가 변경 하나를 통째로 못 본다.
            var changes = new[] { Change("weird.txt", "Added", "@@ -0 +1 @@\n+x") };

            Assert.That(CommitMessageComposer.BuildUserMessage(changes, 400), Does.Contain("weird.txt"));
        }

        [Test]
        public void BuildUserMessage_TranslatesStates_WhenAddedOrDeleted()
        {
            var changes = new[]
            {
                Change("dbo/Tables/Orders.sql", "Added", "@@ -0 +1 @@\n+a"),
                Change("dbo/Views/v_Old.sql", "Deleted", "@@ -1 +0 @@\n-b"),
            };

            var message = CommitMessageComposer.BuildUserMessage(changes, 400);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("추가"));
                Assert.That(message, Does.Contain("삭제"));
            });
        }

        [Test]
        public void BuildUserMessage_TruncatesPatch_WhenDiffExceedsLimit()
        {
            var longPatch = string.Join("\n", Enumerable.Range(0, 100).Select(i => "+line" + i));
            var changes = new[] { Change("dbo/Tables/Orders.sql", "Modified", longPatch) };

            var message = CommitMessageComposer.BuildUserMessage(changes, maxDiffLines: 10);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("-- (이하 생략)"));
                Assert.That(message, Does.Not.Contain("+line99"));
            });
        }

        [Test]
        public void BuildUserMessage_KeepsEveryObjectInList_WhenDiffTruncated()
        {
            // 잘린 diff로도 "무엇이 바뀌었는지"는 말할 수 있어야 한다.
            var longPatch = string.Join("\n", Enumerable.Range(0, 100).Select(i => "+line" + i));
            var changes = new[]
            {
                Change("dbo/Tables/Orders.sql", "Modified", longPatch),
                Change("dbo/Procedures/usp_GetOrder.sql", "Modified", longPatch),
            };

            var message = CommitMessageComposer.BuildUserMessage(changes, maxDiffLines: 6);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("dbo.Orders"));
                Assert.That(message, Does.Contain("dbo.usp_GetOrder"));
            });
        }

        [Test]
        public void SystemPrompt_StatesFormatRules_Always()
        {
            Assert.Multiple(() =>
            {
                Assert.That(CommitMessageComposer.SystemPrompt, Does.Contain("feat"));
                Assert.That(CommitMessageComposer.SystemPrompt, Does.Contain("72"));
            });
        }
    }
```

파일 위쪽에 `using System.Linq;`와 `using DBVC.Core.Models;`를 더한다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CommitMessageComposerPromptTests"`
Expected: FAIL — `DiffFileChange`/`BuildUserMessage`를 찾을 수 없다.

- [ ] **Step 3: 모델을 쓴다**

`src/DBVC.Core/Models/DiffFileChange.cs`:

```csharp
namespace DBVC.Core.Models
{
    /// <summary>
    /// 파일 하나의 변경. <see cref="Patch"/>는 그 파일에 해당하는 unified diff 본문이다.
    ///
    /// 파일별로 나눠 두는 이유는 잘라내기다 — 한 덩어리 문자열이면 앞쪽 객체가 뒤쪽 객체의
    /// 몫까지 먹어 마지막 객체의 변경이 통째로 사라진다.
    /// </summary>
    public class DiffFileChange
    {
        public string RelativePath { get; set; } = string.Empty;

        /// <summary><c>Added</c> · <c>Modified</c> · <c>Deleted</c>. 화면 계층이 쓰는 값과 같은 영어 식별자다.</summary>
        public string Status { get; set; } = string.Empty;

        public string Patch { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 4: 조립을 구현한다**

`src/DBVC.Core/CommitMessageComposer.cs`에 더한다(파일 위쪽에 `using System.Collections.Generic;`, `using System.Text;`, `using DBVC.Core.Models;` 추가).

```csharp
        /// <summary>
        /// 형식을 강제하는 자리. 설명만으로는 준수율이 잘 오르지 않아 예시를 함께 싣는다.
        /// </summary>
        public const string SystemPrompt =
            "당신은 SQL Server 스키마 저장소의 커밋 메시지를 짓는다.\n" +
            "규칙:\n" +
            "- 한국어 한 줄로만 답한다. 설명·머리말·코드펜스를 쓰지 않는다.\n" +
            "- 형식은 `접두어: 본문`이다. 접두어는 feat, fix, refactor, perf, chore 중 하나다.\n" +
            "- 스코프(괄호)를 쓰지 않는다.\n" +
            "- 본문은 명령형 현재시제로 끝낸다(예: ~한다, ~더한다, ~고친다).\n" +
            "- 전체 길이는 72자를 넘지 않는다. 마침표로 끝내지 않는다.\n" +
            "예시:\n" +
            "feat: 주문 조회 프로시저에 취소일자 필터를 더한다\n" +
            "fix: 재고 계산 함수의 반올림 오류를 고친다\n" +
            "chore: 사용하지 않는 임시 테이블을 지운다";

        /// <summary>
        /// 변경 목록과 diff를 담은 user 메시지를 만든다.
        ///
        /// <paramref name="maxDiffLines"/>를 넘으면 diff는 객체별로 균등하게 잘리지만
        /// <b>목록은 전부 남는다</b> — 잘린 diff로도 무엇이 바뀌었는지는 말할 수 있어야 한다.
        /// </summary>
        public static string BuildUserMessage(IReadOnlyList<DiffFileChange> changes, int maxDiffLines)
        {
            if (changes == null || changes.Count == 0) return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine("다음은 이번 커밋에 담기는 데이터베이스 객체의 변경이다.");
            builder.AppendLine();
            builder.AppendLine("[변경 객체]");
            foreach (var change in changes)
            {
                builder.AppendLine($"- {Describe(change.RelativePath)} ({StateText(change.Status)})");
            }

            // 0으로 나누지 않도록 최소 1줄은 준다. 상한이 객체 수보다 작은 경우다.
            var perFile = Math.Max(1, maxDiffLines / changes.Count);

            builder.AppendLine();
            builder.AppendLine("[변경 내용(unified diff)]");
            foreach (var change in changes)
            {
                builder.AppendLine($"--- {change.RelativePath}");
                builder.AppendLine(TrimPatch(change.Patch, perFile));
            }

            builder.AppendLine();
            builder.AppendLine("위 변경을 요약한 커밋 메시지 한 줄을 규칙대로 답한다.");
            return builder.ToString();
        }

        /// <summary>
        /// 저장소 경로를 <c>스키마.객체명</c>으로 옮긴다. 규약 밖 경로는 원문을 그대로 쓴다 —
        /// 조용히 버리면 AI가 변경 하나를 통째로 보지 못한다.
        /// </summary>
        private static string Describe(string relativePath)
        {
            if (ObjectPathConvention.TryParseRelativePath(relativePath, out var schema, out var objectType, out var objectName))
            {
                return $"{schema}.{objectName} ({objectType})";
            }
            return relativePath;
        }

        private static string StateText(string? status) => status switch
        {
            "Added" => "추가",
            "Modified" => "수정",
            "Deleted" => "삭제",
            _ => status ?? string.Empty,
        };

        private static string TrimPatch(string patch, int maxLines)
        {
            if (string.IsNullOrEmpty(patch)) return string.Empty;

            var lines = patch.Replace("\r\n", "\n").Split('\n');
            if (lines.Length <= maxLines) return patch;

            return string.Join("\n", lines.Take(maxLines)) + "\n-- (이하 생략)";
        }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CommitMessageComposer"`
Expected: PASS (Task 3의 9개 + 6개 = 15개)

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Core/Models/DiffFileChange.cs src/DBVC.Core/CommitMessageComposer.cs tests/DBVC.Core.Tests/CommitMessageComposerTests.cs
git commit -m "feat(core): 변경 목록과 diff로 프롬프트를 짓는다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: OpenAI 호환 클라이언트

**Files:**
- Create: `src/DBVC.Core/OpenAiCompatibleClient.cs`
- Test: `tests/DBVC.Core.Tests/OpenAiCompatibleClientTests.cs`

**Interfaces:**
- Consumes: `AiSettings`(Task 2)
- Produces:
  - `AiRequestException : Exception` — 메시지가 한국어 사유다
  - `IChatCompletionClient { Task<string> CompleteAsync(AiSettings settings, string systemPrompt, string userMessage, CancellationToken ct); }`
  - `OpenAiCompatibleClient(HttpMessageHandler? handler = null)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class OpenAiCompatibleClientTests
    {
        /// <summary>요청을 기록하고 정해진 응답을 돌려준다. 네트워크를 타지 않는다.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;

            public StubHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
            }
        }

        private const string SuccessBody =
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"feat: 주문 뷰를 더한다\"}}]}";

        private static AiSettings Settings(string baseUrl = "https://llm.example.com/v1", string apiKey = "sk-test") =>
            new AiSettings { BaseUrl = baseUrl, Model = "test-model", ApiKey = apiKey, TimeoutSeconds = 30 };

        [Test]
        public async Task CompleteAsync_ReturnsContent_WhenResponseIsSuccessful()
        {
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            var result = await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None);

            Assert.That(result, Is.EqualTo("feat: 주문 뷰를 더한다"));
        }

        [Test]
        public async Task CompleteAsync_PostsToChatCompletions_WhenBaseUrlHasTrailingSlash()
        {
            // 사용자가 끝에 슬래시를 붙여 넣는 일은 흔하다. 두 형태가 같은 URL이 되어야 한다.
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            await client.CompleteAsync(Settings("https://llm.example.com/v1/"), "sys", "user", CancellationToken.None);

            Assert.That(
                handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("https://llm.example.com/v1/chat/completions"));
        }

        [Test]
        public async Task CompleteAsync_SendsBearerHeader_WhenApiKeyPresent()
        {
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None);

            Assert.That(handler.LastRequest!.Headers.Authorization!.ToString(), Is.EqualTo("Bearer sk-test"));
        }

        [Test]
        public async Task CompleteAsync_OmitsAuthorizationHeader_WhenApiKeyEmpty()
        {
            // 사내 LLM 서버는 인증 없이 열려 있는 경우가 있다. 빈 Bearer를 보내면 거부하는 구현이 있다.
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            await client.CompleteAsync(Settings(apiKey: string.Empty), "sys", "user", CancellationToken.None);

            Assert.That(handler.LastRequest!.Headers.Authorization, Is.Null);
        }

        [Test]
        public async Task CompleteAsync_SendsModelAndMessages_WhenCalled()
        {
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            await client.CompleteAsync(Settings(), "시스템 지시", "사용자 내용", CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(handler.LastBody, Does.Contain("\"model\":\"test-model\""));
                Assert.That(handler.LastBody, Does.Contain("시스템 지시"));
                Assert.That(handler.LastBody, Does.Contain("사용자 내용"));
            });
        }

        [TestCase(HttpStatusCode.Unauthorized, "API 키")]
        [TestCase(HttpStatusCode.Forbidden, "API 키")]
        [TestCase(HttpStatusCode.NotFound, "주소")]
        [TestCase((HttpStatusCode)429, "한도")]
        [TestCase(HttpStatusCode.InternalServerError, "응답하지 못했습니다")]
        public void CompleteAsync_ThrowsKoreanReason_WhenStatusIsError(HttpStatusCode status, string expectedFragment)
        {
            var client = new OpenAiCompatibleClient(new StubHandler(status, "{\"error\":\"nope\"}"));

            var ex = Assert.ThrowsAsync<AiRequestException>(async () =>
                await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain(expectedFragment));
        }

        [Test]
        public void CompleteAsync_Throws_WhenResponseHasNoChoices()
        {
            var client = new OpenAiCompatibleClient(new StubHandler(HttpStatusCode.OK, "{\"choices\":[]}"));

            var ex = Assert.ThrowsAsync<AiRequestException>(async () =>
                await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("응답"));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~OpenAiCompatibleClientTests"`
Expected: FAIL — `OpenAiCompatibleClient`를 찾을 수 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Core/OpenAiCompatibleClient.cs`:

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// AI 호출이 실패한 사유. 메시지는 그대로 화면에 뜨므로 한국어다.
    /// </summary>
    public class AiRequestException : Exception
    {
        public AiRequestException(string message) : base(message) { }
        public AiRequestException(string message, Exception inner) : base(message, inner) { }
    }

    public interface IChatCompletionClient
    {
        Task<string> CompleteAsync(AiSettings settings, string systemPrompt, string userMessage, CancellationToken cancellationToken);
    }

    /// <summary>
    /// OpenAI 호환 <c>/chat/completions</c>를 부른다. 사내 LLM 서버와 외부 상용 API가
    /// 같은 규약을 쓰므로 구현은 하나로 족하다.
    /// </summary>
    public class OpenAiCompatibleClient : IChatCompletionClient
    {
        private readonly HttpClient _httpClient;

        /// <param name="handler">
        /// 테스트가 네트워크 없이 요청을 들여다보기 위한 이음매. 실제 실행에서는 null이다.
        /// </param>
        public OpenAiCompatibleClient(HttpMessageHandler? handler = null)
        {
            // HttpClient는 한 번 만들어 재사용한다. 호출마다 만들면 소켓이 고갈된다.
            _httpClient = handler == null ? new HttpClient() : new HttpClient(handler);
        }

        public async Task<string> CompleteAsync(
            AiSettings settings, string systemPrompt, string userMessage, CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.BaseUrl));

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                // 빈 Bearer를 보내면 거부하는 구현이 있어, 키가 없으면 헤더 자체를 달지 않는다.
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }

            var payload = new
            {
                model = settings.Model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage },
                },
                temperature = 0.2,
                max_tokens = 200,
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 30));

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiRequestException(
                    $"AI 서버가 {settings.TimeoutSeconds}초 안에 응답하지 않았습니다. 주소와 네트워크를 확인하세요.");
            }
            catch (HttpRequestException ex)
            {
                throw new AiRequestException(
                    $"AI 서버에 연결하지 못했습니다. 주소를 확인하세요.\n\n{ex.Message}", ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new AiRequestException(DescribeFailure(response.StatusCode, body));
                }

                return ExtractContent(body);
            }
        }

        /// <summary>
        /// 끝 슬래시 유무와 무관하게 같은 URL이 되게 한다. 사용자가 붙여 넣는 값이라
        /// 두 형태가 모두 온다.
        /// </summary>
        private static string BuildEndpoint(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new AiRequestException("AI 프로바이더 주소가 설정되지 않았습니다.");
            }
            return baseUrl.TrimEnd('/') + "/chat/completions";
        }

        /// <summary>서버가 돌려준 영문 본문은 인용으로만 싣는다.</summary>
        private static string DescribeFailure(HttpStatusCode status, string body)
        {
            var reason = status switch
            {
                HttpStatusCode.Unauthorized => "AI 서버가 API 키를 거부했습니다.",
                HttpStatusCode.Forbidden => "AI 서버가 API 키의 권한을 거부했습니다.",
                HttpStatusCode.NotFound => "AI 서버에서 해당 주소를 찾지 못했습니다. 프로바이더 주소와 모델 이름을 확인하세요.",
                (HttpStatusCode)429 => "AI 서버의 호출 한도를 넘었습니다. 잠시 뒤에 다시 시도하세요.",
                _ => $"AI 서버가 요청에 응답하지 못했습니다 (HTTP {(int)status}).",
            };

            return string.IsNullOrWhiteSpace(body) ? reason : reason + "\n\n" + Shorten(body);
        }

        private static string Shorten(string body) =>
            body.Length <= 500 ? body : body.Substring(0, 500) + "...";

        private static string ExtractContent(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("choices", out var choices)
                    && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content))
                {
                    return content.GetString() ?? string.Empty;
                }
            }
            catch (JsonException ex)
            {
                throw new AiRequestException(
                    $"AI 서버의 응답을 해석하지 못했습니다.\n\n{Shorten(body)}", ex);
            }

            throw new AiRequestException($"AI 서버가 빈 응답을 돌려주었습니다.\n\n{Shorten(body)}");
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~OpenAiCompatibleClientTests"`
Expected: PASS (11개 — `TestCase` 5개 포함)

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/OpenAiCompatibleClient.cs tests/DBVC.Core.Tests/OpenAiCompatibleClientTests.cs
git commit -m "feat(core): OpenAI 호환 채팅 완성 클라이언트를 더한다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: 작업 트리 diff 추출

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs` (`IGitManager`에 메서드 추가)
- Modify: `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs` (테스트 추가)

**Interfaces:**
- Consumes: `DiffFileChange`(Task 4)
- Produces: `IGitManager.GetUnifiedDiff(string serverName, string databaseName, IEnumerable<string> relativePaths) → IReadOnlyList<DiffFileChange>`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs`에 더한다. 기존 헬퍼(`NewTempDir`, `SeedIdentity`)를 그대로 쓴다.

```csharp
        [Test]
        public void GetUnifiedDiff_ReturnsModifiedPatch_WhenWorkingTreeChanged()
        {
            var repoPath = NewTempDir();
            Repository.Init(repoPath);
            SeedIdentity(repoPath);
            var relativePath = "dbo/Tables/Orders.sql";
            var fullPath = Path.Combine(repoPath, "dbo", "Tables", "Orders.sql");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "CREATE TABLE dbo.Orders (Id INT);\n");

            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                repo.Commit("init", TestSignature, TestSignature);
            }

            File.WriteAllText(fullPath, "CREATE TABLE dbo.Orders (Id INT, Memo NVARCHAR(50));\n");

            var config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            config.AddMapping(Server, Database, repoPath);
            var manager = new GitManager(config);

            var changes = manager.GetUnifiedDiff(Server, Database, new[] { relativePath });

            Assert.Multiple(() =>
            {
                Assert.That(changes, Has.Count.EqualTo(1));
                Assert.That(changes[0].RelativePath, Is.EqualTo(relativePath));
                Assert.That(changes[0].Status, Is.EqualTo("Modified"));
                Assert.That(changes[0].Patch, Does.Contain("Memo"));
            });
        }

        [Test]
        public void GetUnifiedDiff_ReportsAdded_WhenFileIsUntracked()
        {
            // 새 객체는 추적되지 않은 상태로 나타난다. 이것을 빠뜨리면 신규 생성이 AI에게 보이지 않는다.
            var repoPath = NewTempDir();
            Repository.Init(repoPath);
            SeedIdentity(repoPath);
            var seed = Path.Combine(repoPath, "seed.txt");
            File.WriteAllText(seed, "x");
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                repo.Commit("init", TestSignature, TestSignature);
            }

            var relativePath = "dbo/Views/v_New.sql";
            var fullPath = Path.Combine(repoPath, "dbo", "Views", "v_New.sql");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "CREATE VIEW dbo.v_New AS SELECT 1 AS One;\n");

            var config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            config.AddMapping(Server, Database, repoPath);
            var manager = new GitManager(config);

            var changes = manager.GetUnifiedDiff(Server, Database, new[] { relativePath });

            Assert.Multiple(() =>
            {
                Assert.That(changes, Has.Count.EqualTo(1));
                Assert.That(changes[0].Status, Is.EqualTo("Added"));
                Assert.That(changes[0].Patch, Does.Contain("v_New"));
            });
        }

        [Test]
        public void GetUnifiedDiff_ReturnsEmpty_WhenMappingMissing()
        {
            var config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            var manager = new GitManager(config);

            Assert.That(manager.GetUnifiedDiff(Server, Database, new[] { "dbo/Tables/Orders.sql" }), Is.Empty);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GetUnifiedDiff"`
Expected: FAIL — `GetUnifiedDiff` 메서드가 없다.

- [ ] **Step 3: 인터페이스에 더한다**

`src/DBVC.Core/Abstractions.cs`의 `IGitManager`에서 `GetFileContentAtHead` 선언 위에 더한다.

```csharp
        /// <summary>
        /// 선택한 경로들의 작업 트리와 <c>HEAD</c> 차이를 파일별 unified diff로 낸다.
        /// 매핑이 없으면 빈 목록이다.
        ///
        /// 추적되지 않은 파일(새 객체)도 포함한다 — 빠뜨리면 신규 생성이 통째로 보이지 않는다.
        /// </summary>
        IReadOnlyList<DiffFileChange> GetUnifiedDiff(
            string serverName, string databaseName, IEnumerable<string> relativePaths);
```

- [ ] **Step 4: 구현을 쓴다**

`src/DBVC.Core/GitManager.cs`의 `GetFileContentAtHead` 위에 더한다.

```csharp
        /// <summary>
        /// AI 커밋 메시지 생성이 쓰는 diff. 화면의 Diff 보기와 같은 출처(작업 트리 vs HEAD)를
        /// 보는 것이 중요하다 — 다르면 "왜 엉뚱한 요약이 나왔나"를 추적할 방법이 없다.
        /// </summary>
        public IReadOnlyList<DiffFileChange> GetUnifiedDiff(
            string serverName, string databaseName, IEnumerable<string> relativePaths)
        {
            var result = new List<DiffFileChange>();
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null || relativePaths == null) return result;

            var paths = relativePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0) return result;

            try
            {
                using var repo = new Repository(repoPath);

                // IncludeUntracked가 있어야 새 객체가 잡힌다. 비교 대상을 HEAD 트리로 두는 것은
                // 스테이징 여부와 무관하게 "마지막 커밋과의 차이"를 보기 위해서다.
                var patch = repo.Diff.Compare<Patch>(
                    repo.Head.Tip?.Tree,
                    DiffTargets.WorkingDirectory,
                    paths,
                    new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false },
                    new CompareOptions { ContextLines = 3 });

                foreach (var entry in patch)
                {
                    result.Add(new DiffFileChange
                    {
                        RelativePath = entry.Path,
                        Status = DescribeStatus(entry.Status),
                        Patch = entry.Patch ?? string.Empty,
                    });
                }
            }
            catch (Exception ex)
            {
                // 여기서 던지면 버튼 하나가 도구 창 전체를 내린다. 빈 목록이면 호출자가
                // "변경 내용을 읽지 못했습니다"로 갈라 안내한다.
                Debug.WriteLine($"GitManager.GetUnifiedDiff failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 화면 계층이 쓰는 영어 식별자와 같은 값으로 옮긴다(ChangeItemViewModel.State).
        /// 한국어로 옮기는 자리는 그쪽 하나뿐이다.
        /// </summary>
        private static string DescribeStatus(ChangeKind kind) => kind switch
        {
            ChangeKind.Added => "Added",
            ChangeKind.Untracked => "Added",
            ChangeKind.Deleted => "Deleted",
            _ => "Modified",
        };
```

파일 위쪽 `using`에 `using DBVC.Core.Models;`가 이미 있는지 확인하고, 없으면 더한다.

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GetUnifiedDiff"`
Expected: PASS (3개)

- [ ] **Step 6: Core 전체가 여전히 통과하는지 본다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS (SQL Server가 없으면 `SmoManagerIntegrationTests`는 Skip)

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 선택한 경로의 작업 트리 diff를 낸다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: 전송 동의 판정과 생성기 조립

**Files:**
- Create: `src/DBVC.Core/AiConsent.cs`, `src/DBVC.Core/CommitMessageGenerator.cs`
- Modify: `src/DBVC.Core/Abstractions.cs` (`IAiCommitMessageGenerator` 추가)
- Test: `tests/DBVC.Core.Tests/AiConsentTests.cs`, `tests/DBVC.Core.Tests/CommitMessageGeneratorTests.cs`

**Interfaces:**
- Consumes: `IAiSettingsStore`(Task 2), `CommitMessageComposer`(Task 3·4), `IChatCompletionClient`(Task 5), `IGitManager.GetUnifiedDiff`(Task 6)
- Produces:
  - `AiConsent.HostOf(string? baseUrl) → string`
  - `AiConsent.NeedsConsent(AiSettings settings) → bool`
  - `IAiCommitMessageGenerator { string Generate(string serverName, string databaseName, IEnumerable<string> relativePaths, CancellationToken ct); }`
  - `CommitMessageGenerator(IGitManager gitManager, IAiSettingsStore settingsStore, IChatCompletionClient client)`

- [ ] **Step 1: 동의 판정의 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/AiConsentTests.cs`:

```csharp
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class AiConsentTests
    {
        [Test]
        public void HostOf_ReturnsHost_WhenUrlIsAbsolute()
        {
            Assert.That(AiConsent.HostOf("https://api.openai.com/v1"), Is.EqualTo("api.openai.com"));
        }

        [Test]
        public void HostOf_ReturnsRawValue_WhenUrlIsNotParsable()
        {
            // 판정이 조용히 빈 문자열이 되면 동의를 묻지 않고 전송해 버린다.
            Assert.That(AiConsent.HostOf("llm.example.com/v1"), Is.EqualTo("llm.example.com/v1"));
        }

        [Test]
        public void NeedsConsent_ReturnsTrue_WhenNoConsentRecorded()
        {
            var settings = new AiSettings { BaseUrl = "https://api.openai.com/v1", Model = "m" };

            Assert.That(AiConsent.NeedsConsent(settings), Is.True);
        }

        [Test]
        public void NeedsConsent_ReturnsFalse_WhenHostAlreadyConsented()
        {
            var settings = new AiSettings
            {
                BaseUrl = "https://api.openai.com/v1",
                Model = "m",
                ConsentedHost = "api.openai.com",
            };

            Assert.That(AiConsent.NeedsConsent(settings), Is.False);
        }

        [Test]
        public void NeedsConsent_ReturnsTrue_WhenHostChanged()
        {
            // 동의는 "AI를 쓰는 것"이 아니라 "이 목적지로 보내는 것"에 대한 것이다.
            var settings = new AiSettings
            {
                BaseUrl = "https://other-llm.example.com/v1",
                Model = "m",
                ConsentedHost = "api.openai.com",
            };

            Assert.That(AiConsent.NeedsConsent(settings), Is.True);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~AiConsentTests"`
Expected: FAIL — `AiConsent`를 찾을 수 없다.

- [ ] **Step 3: `AiConsent`를 쓴다**

`src/DBVC.Core/AiConsent.cs`:

```csharp
using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// "어디로 나가는지 모른 채 운영 DDL이 나가는 일"을 막는 판정.
    /// 확인 대화상자를 띄우는 것은 화면의 몫이고, 물어야 하는지는 여기가 정한다.
    /// </summary>
    public static class AiConsent
    {
        /// <summary>
        /// 주소에서 호스트만 뽑는다. 해석하지 못하면 원문을 그대로 돌려준다 —
        /// 빈 문자열을 돌려주면 동의를 묻지 않고 전송하는 경로가 생긴다.
        /// </summary>
        public static string HostOf(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;

            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host
                : baseUrl!.Trim();
        }

        /// <summary>이 설정의 목적지로 보내기 전에 사용자에게 물어야 하는지.</summary>
        public static bool NeedsConsent(AiSettings settings)
        {
            if (settings == null) return true;

            var host = HostOf(settings.BaseUrl);
            return !string.Equals(host, settings.ConsentedHost, StringComparison.OrdinalIgnoreCase);
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~AiConsentTests"`
Expected: PASS (5개)

- [ ] **Step 5: 생성기의 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/CommitMessageGeneratorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class CommitMessageGeneratorTests
    {
        private sealed class StubSettingsStore : IAiSettingsStore
        {
            public AiSettings Settings { get; set; } = new AiSettings
            {
                BaseUrl = "https://llm.example.com/v1",
                Model = "m",
                MaxDiffLines = 400,
            };

            public AiSettings Load() => Settings;
            public void Save(AiSettings settings) => Settings = settings;
        }

        private sealed class StubClient : IChatCompletionClient
        {
            public string Response { get; set; } = "feat: 주문 뷰를 더한다";
            public string? LastUserMessage { get; private set; }
            public int CallCount { get; private set; }

            public Task<string> CompleteAsync(
                AiSettings settings, string systemPrompt, string userMessage, CancellationToken cancellationToken)
            {
                CallCount++;
                LastUserMessage = userMessage;
                return Task.FromResult(Response);
            }
        }

        /// <summary>GetUnifiedDiff만 답하는 최소 대역. 나머지는 이 테스트가 부르지 않는다.</summary>
        private sealed class StubGitManager : FakeGitManagerBase
        {
            public List<DiffFileChange> Changes { get; } = new List<DiffFileChange>();

            public override IReadOnlyList<DiffFileChange> GetUnifiedDiff(
                string serverName, string databaseName, IEnumerable<string> relativePaths) => Changes;
        }

        private static CommitMessageGenerator Build(StubGitManager git, StubSettingsStore store, StubClient client) =>
            new CommitMessageGenerator(git, store, client);

        [Test]
        public void Generate_ReturnsCleanedMessage_WhenDiffPresent()
        {
            var git = new StubGitManager();
            git.Changes.Add(new DiffFileChange
            {
                RelativePath = "dbo/Views/v_Order.sql", Status = "Added", Patch = "@@ -0 +1 @@\n+CREATE VIEW",
            });
            var client = new StubClient { Response = "```\nfeat: 주문 뷰를 더한다\n```" };

            var message = Build(git, new StubSettingsStore(), client)
                .Generate("localhost", "testdb", new[] { "dbo/Views/v_Order.sql" }, CancellationToken.None);

            Assert.That(message, Is.EqualTo("feat: 주문 뷰를 더한다"));
        }

        [Test]
        public void Generate_SendsObjectNameInPrompt_WhenDiffPresent()
        {
            var git = new StubGitManager();
            git.Changes.Add(new DiffFileChange
            {
                RelativePath = "dbo/Views/v_Order.sql", Status = "Added", Patch = "@@ -0 +1 @@\n+CREATE VIEW",
            });
            var client = new StubClient();

            Build(git, new StubSettingsStore(), client)
                .Generate("localhost", "testdb", new[] { "dbo/Views/v_Order.sql" }, CancellationToken.None);

            Assert.That(client.LastUserMessage, Does.Contain("dbo.v_Order"));
        }

        [Test]
        public void Generate_Throws_WhenSettingsIncomplete()
        {
            var store = new StubSettingsStore { Settings = new AiSettings() };
            var client = new StubClient();

            var ex = Assert.Throws<AiRequestException>(() =>
                Build(new StubGitManager(), store, client)
                    .Generate("localhost", "testdb", new[] { "a.sql" }, CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("옵션"));
                Assert.That(client.CallCount, Is.Zero);
            });
        }

        [Test]
        public void Generate_Throws_WhenNoDiffFound()
        {
            // 변경이 없는데 호출하면 모델이 문장을 지어낸다. 부르기 전에 멈춘다.
            var client = new StubClient();

            var ex = Assert.Throws<AiRequestException>(() =>
                Build(new StubGitManager(), new StubSettingsStore(), client)
                    .Generate("localhost", "testdb", new[] { "a.sql" }, CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("변경"));
                Assert.That(client.CallCount, Is.Zero);
            });
        }

        [Test]
        public void Generate_Throws_WhenModelReturnsBlank()
        {
            var git = new StubGitManager();
            git.Changes.Add(new DiffFileChange { RelativePath = "dbo/Views/v.sql", Status = "Added", Patch = "+x" });
            var client = new StubClient { Response = "   " };

            var ex = Assert.Throws<AiRequestException>(() =>
                Build(git, new StubSettingsStore(), client)
                    .Generate("localhost", "testdb", new[] { "dbo/Views/v.sql" }, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("빈"));
        }
    }
}
```

> **`FakeGitManagerBase`가 필요하다.** `IGitManager`는 메서드가 많아 테스트마다 전부 구현하면 잡음이 된다. `tests/DBVC.Core.Tests/FakeGitManagerBase.cs`를 만들어 모든 멤버를 `virtual`로 두고 `NotSupportedException`을 던지게 한다 — 테스트가 부르지 않는 메서드가 조용히 기본값을 돌려주면, 잘못된 경로를 탔을 때 테스트가 초록으로 남는다.

- [ ] **Step 6: `FakeGitManagerBase`를 쓴다**

`tests/DBVC.Core.Tests/FakeGitManagerBase.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// <see cref="IGitManager"/>의 테스트 대역 뼈대. 부르지 않을 메서드는 던진다 —
    /// 조용히 기본값을 돌려주면 잘못된 경로를 탄 테스트가 초록으로 남는다.
    /// </summary>
    internal abstract class FakeGitManagerBase : IGitManager
    {
        public virtual bool IsRepository(string path) => throw new NotSupportedException();
        public virtual string CloneRepository(string remoteUrl, string targetPath, IProgress<CloneProgress>? progress, CancellationToken cancellationToken, string? branchName = null) => throw new NotSupportedException();
        public virtual RepositoryState? GetRepositoryState(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual string GetStatus(string repoPath) => throw new NotSupportedException();
        public virtual string GetStatusForDatabase(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual IReadOnlyList<string> GetChangedFiles(string repoPath) => throw new NotSupportedException();
        public virtual IReadOnlyDictionary<string, string> GetChangedFileStates(string repoPath) => throw new NotSupportedException();
        public virtual GitCommitResult CommitChanges(string serverName, string databaseName, string message, IEnumerable<string>? relativePaths = null) => throw new NotSupportedException();
        public virtual DiscardResult DiscardChanges(string serverName, string databaseName, IEnumerable<string> relativePaths) => throw new NotSupportedException();
        public virtual PullResult PullChanges(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual IReadOnlyList<BranchInfo> GetBranches(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual BranchResult CreateBranch(string serverName, string databaseName, string branchName) => throw new NotSupportedException();
        public virtual BranchResult SwitchBranch(string serverName, string databaseName, string branchName) => throw new NotSupportedException();
        public virtual PushResult PushChanges(string serverName, string databaseName, bool setUpstream = false) => throw new NotSupportedException();
        public virtual bool HasCommitsToPush(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual RemoteStatus FetchRemoteStatus(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual IReadOnlyList<DiffFileChange> GetUnifiedDiff(string serverName, string databaseName, IEnumerable<string> relativePaths) => throw new NotSupportedException();
        public virtual IReadOnlyList<CommitInfo> GetHistory(string serverName, string databaseName, string? relativeFilePath) => throw new NotSupportedException();
        public virtual string? GetFileContentAtHead(string serverName, string databaseName, string relativeFilePath) => throw new NotSupportedException();
        public virtual string? GetFileContentBeforeLastCommit(string serverName, string databaseName, string relativeFilePath) => throw new NotSupportedException();
        public virtual CommitDetail GetCommitDetail(string serverName, string databaseName, string commitSha, string? relativeFilePath) => throw new NotSupportedException();
    }
}
```

> 구현 시점의 `IGitManager` 실제 선언을 보고 목록을 맞춘다. 컴파일 오류가 남으면 그 메서드를 같은 형태로 더한다.

- [ ] **Step 7: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CommitMessageGeneratorTests"`
Expected: FAIL — `CommitMessageGenerator`를 찾을 수 없다.

- [ ] **Step 8: 인터페이스와 생성기를 쓴다**

`src/DBVC.Core/Abstractions.cs`에 더한다(`ISqlCredentialStore` 아래).

```csharp
    /// <summary>
    /// 선택한 변경으로 커밋 메시지 초안을 만든다. UI가 네트워크 없이 테스트되도록 하는 이음매다.
    /// </summary>
    public interface IAiCommitMessageGenerator
    {
        /// <summary>
        /// 실패는 전부 <see cref="AiRequestException"/>이고, 메시지가 그대로 화면에 뜨는 한국어 사유다.
        /// </summary>
        string Generate(
            string serverName, string databaseName, IEnumerable<string> relativePaths, CancellationToken cancellationToken);
    }
```

`src/DBVC.Core/CommitMessageGenerator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// diff 추출 → 프롬프트 조립 → 호출 → 정제를 잇는다. 판단은 각 조각이 하고
    /// 여기는 순서만 정한다.
    /// </summary>
    public class CommitMessageGenerator : IAiCommitMessageGenerator
    {
        private readonly IGitManager _gitManager;
        private readonly IAiSettingsStore _settingsStore;
        private readonly IChatCompletionClient _client;

        public CommitMessageGenerator(
            IGitManager gitManager, IAiSettingsStore settingsStore, IChatCompletionClient client)
        {
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public string Generate(
            string serverName, string databaseName, IEnumerable<string> relativePaths, CancellationToken cancellationToken)
        {
            var settings = _settingsStore.Load();
            if (!settings.IsConfigured)
            {
                throw new AiRequestException(
                    "AI 설정이 없습니다. 도구 > 옵션 > DBVC > AI 커밋 메시지에서 프로바이더 주소와 모델을 설정하세요.");
            }

            var changes = _gitManager.GetUnifiedDiff(serverName, databaseName, relativePaths ?? Enumerable.Empty<string>());
            if (changes == null || changes.Count == 0)
            {
                // 변경이 없는데 부르면 모델이 문장을 지어낸다.
                throw new AiRequestException("선택한 항목에서 읽어 낼 변경 내용이 없습니다.");
            }

            var userMessage = CommitMessageComposer.BuildUserMessage(changes, settings.MaxDiffLines);

            // 호출자(ViewModel)가 이미 백그라운드 스레드에 있다. UI 스레드가 아니므로
            // 여기서 기다려도 교착하지 않는다 — 이 메서드를 UI 스레드에서 부르면 안 된다.
            var raw = _client
                .CompleteAsync(settings, CommitMessageComposer.SystemPrompt, userMessage, cancellationToken)
                .GetAwaiter()
                .GetResult();

            var message = CommitMessageComposer.Clean(raw);
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new AiRequestException("AI가 빈 응답을 돌려주었습니다. 다시 시도하거나 직접 입력하세요.");
            }

            return message;
        }
    }
}
```

- [ ] **Step 9: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CommitMessageGeneratorTests"`
Expected: PASS (5개)

- [ ] **Step 10: Core 전체를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS

- [ ] **Step 11: 커밋**

```bash
git add src/DBVC.Core/AiConsent.cs src/DBVC.Core/CommitMessageGenerator.cs src/DBVC.Core/Abstractions.cs tests/DBVC.Core.Tests/AiConsentTests.cs tests/DBVC.Core.Tests/CommitMessageGeneratorTests.cs tests/DBVC.Core.Tests/FakeGitManagerBase.cs
git commit -m "feat(core): 변경에서 커밋 메시지 초안을 만드는 생성기를 더한다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: ViewModel 명령과 버튼

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml:195-198`
- Modify: `src/DBVC.Vsix/DbvcServices.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/AiCommitMessageTests.cs`

**Interfaces:**
- Consumes: `IAiCommitMessageGenerator`, `IAiSettingsStore`, `AiConsent`, `AiRequestException`(Task 7)
- Produces:
  - `ViewChangesViewModel` 생성자에 선택 인자 `IAiCommitMessageGenerator? aiGenerator = null, IAiSettingsStore? aiSettingsStore = null` 추가
  - `ViewChangesViewModel.GenerateCommitMessageCommand` (`ICommand`)
  - `DbvcServices.AiSettingsStore`, `DbvcServices.AiCommitMessageGenerator` 속성

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/AiCommitMessageTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Services;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    [TestFixture]
    public class AiCommitMessageTests
    {
        private sealed class StubGenerator : IAiCommitMessageGenerator
        {
            public string Message { get; set; } = "feat: 주문 뷰를 더한다";
            public Exception? ToThrow { get; set; }
            public int CallCount { get; private set; }
            public List<string> LastPaths { get; } = new List<string>();

            public string Generate(
                string serverName, string databaseName, IEnumerable<string> relativePaths, CancellationToken cancellationToken)
            {
                CallCount++;
                LastPaths.Clear();
                LastPaths.AddRange(relativePaths);
                if (ToThrow != null) throw ToThrow;
                return Message;
            }
        }

        private sealed class StubSettingsStore : IAiSettingsStore
        {
            public AiSettings Settings { get; set; } = new AiSettings
            {
                BaseUrl = "https://llm.example.com/v1",
                Model = "m",
                ConsentedHost = "llm.example.com",
            };

            public AiSettings Load() => Settings;
            public void Save(AiSettings settings) => Settings = settings;
        }

        private const string Server = "LocalServer";
        private const string Database = "SalesDB";
        private const string ChangedPath = "dbo/Views/v_Order.sql";

        private Mock<IConfigManager> _config = null!;
        private Mock<IStateTracker> _stateTracker = null!;
        private Mock<IGitManager> _git = null!;
        private Mock<ISmoManager> _smo = null!;
        private Mock<IWorkingTreeCleaner> _cleaner = null!;
        private Mock<ISqlCredentialStore> _credentials = null!;
        private Mock<ISsmsConnectionSource> _ssms = null!;

        /// <summary>
        /// 매핑·초기화된 정상 상태. ViewChangesViewModelTests의 준비 절차와 같은 값을 쓴다 —
        /// 두 픽스처가 다른 전제 위에 서면 한쪽만 깨졌을 때 원인을 찾기 어렵다.
        /// </summary>
        [SetUp]
        public void SetUpMocks()
        {
            _config = new Mock<IConfigManager>();
            _stateTracker = new Mock<IStateTracker>();
            _git = new Mock<IGitManager>();
            _smo = new Mock<ISmoManager>();
            _cleaner = new Mock<IWorkingTreeCleaner>();
            _credentials = new Mock<ISqlCredentialStore>();
            _ssms = new Mock<ISsmsConnectionSource>();

            _config.Setup(c => c.TryGetMapping(Server, Database))
                .Returns(new MappingConfig { ServerName = Server, DatabaseName = Database, GitPath = @"C:\repo" });
            _stateTracker.Setup(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(StateTracker.RequiredSchemaVersion);
            _stateTracker.Setup(s => s.TestConnection(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
            _stateTracker.Setup(s => s.RefreshState(Server, Database, It.IsAny<bool>())).Returns(true);
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>());
            _smo.Setup(s => s.ScriptObjectsDetailed(
                    Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(new ScriptResult());
            _git.Setup(g => g.GetChangedFiles(It.IsAny<string>())).Returns(new List<string>());
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None });
            _git.Setup(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new List<CommitInfo>());
            _cleaner.Setup(c => c.RemoveDeletedObjectFiles(It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()))
                .Returns(new CleanupResult());
            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(new SsmsConnectionInfo(Server, Database, SqlAuthMode.Windows, null, null, null));
        }

        /// <summary>
        /// 접속을 마치고 변경 하나가 선택된 ViewModel.
        ///
        /// 스케줄러가 인라인인 것이 요점이다 — 지연 실행이면 Execute 직후에 결과를 볼 수 없다.
        /// </summary>
        private ViewChangesViewModel BuildViewModel(
            StubGenerator generator, StubSettingsStore store, RecordingNotifier notifier)
        {
            var vm = new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, notifier,
                saveDialog: null,
                cleaner: _cleaner.Object,
                connectDialog: null,
                credentialStore: _credentials.Object,
                ssmsConnectionSource: _ssms.Object,
                scheduler: new InlineBackgroundScheduler(),
                aiGenerator: generator,
                aiSettingsStore: store);

            vm.ConnectCommand.Execute(null);

            // 목록은 새로고침이 채우지만, 이 픽스처가 보는 것은 AI 경로뿐이라 직접 넣는다.
            vm.Changes.Add(new ChangeItemViewModel
            {
                ObjectName = "dbo.v_Order",
                ObjectType = "VIEW",
                State = "Added",
                RelativePath = ChangedPath,
                IsSelected = true,
            });

            return vm;
        }

        [Test]
        public void GenerateCommitMessage_FillsMessage_WhenGeneratorSucceeds()
        {
            var generator = new StubGenerator();
            var notifier = new RecordingNotifier();
            var vm = BuildViewModel(generator, new StubSettingsStore(), notifier);

            vm.GenerateCommitMessageCommand.Execute(null);

            Assert.That(vm.CommitMessage, Is.EqualTo("feat: 주문 뷰를 더한다"));
        }

        [Test]
        public void GenerateCommitMessage_KeepsExistingMessage_WhenGeneratorFails()
        {
            // 실패했다고 사용자가 적던 것까지 잃으면 안 된다.
            var generator = new StubGenerator { ToThrow = new AiRequestException("AI 서버에 연결하지 못했습니다.") };
            var notifier = new RecordingNotifier { ConfirmResult = true };
            var vm = BuildViewModel(generator, new StubSettingsStore(), notifier);
            vm.CommitMessage = "손으로 적던 문장";

            vm.GenerateCommitMessageCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(vm.CommitMessage, Is.EqualTo("손으로 적던 문장"));
                Assert.That(notifier.Errors, Has.Some.Contains("연결하지 못했습니다"));
            });
        }

        [Test]
        public void GenerateCommitMessage_AsksBeforeOverwrite_WhenMessageAlreadyTyped()
        {
            var generator = new StubGenerator();
            var notifier = new RecordingNotifier { ConfirmResult = false };
            var vm = BuildViewModel(generator, new StubSettingsStore(), notifier);
            vm.CommitMessage = "손으로 적던 문장";

            vm.GenerateCommitMessageCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(generator.CallCount, Is.Zero);
                Assert.That(vm.CommitMessage, Is.EqualTo("손으로 적던 문장"));
            });
        }

        [Test]
        public void GenerateCommitMessage_DoesNotCall_WhenConsentDeclined()
        {
            // 동의하지 않았는데 DDL이 나가는 경로가 있으면 안 된다.
            var generator = new StubGenerator();
            var store = new StubSettingsStore
            {
                Settings = new AiSettings { BaseUrl = "https://api.openai.com/v1", Model = "m", ConsentedHost = null },
            };
            var notifier = new RecordingNotifier { ConfirmResult = false };
            var vm = BuildViewModel(generator, store, notifier);

            vm.GenerateCommitMessageCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(generator.CallCount, Is.Zero);
                Assert.That(notifier.ConfirmCalls, Has.Some.Matches<(string Title, string Message)>(
                    call => call.Message.Contains("api.openai.com")));
            });
        }

        [Test]
        public void GenerateCommitMessage_RecordsConsent_WhenUserAgrees()
        {
            var generator = new StubGenerator();
            var store = new StubSettingsStore
            {
                Settings = new AiSettings { BaseUrl = "https://api.openai.com/v1", Model = "m", ConsentedHost = null },
            };
            var vm = BuildViewModel(generator, store, new RecordingNotifier { ConfirmResult = true });

            vm.GenerateCommitMessageCommand.Execute(null);

            // 다시 묻지 않아야 한다.
            Assert.That(store.Settings.ConsentedHost, Is.EqualTo("api.openai.com"));
        }

        [Test]
        public void GenerateCommitMessage_ShowsGuidance_WhenSettingsMissing()
        {
            // 버튼을 잠그지 않는 대신, 누르면 어디서 설정하는지 알려 준다.
            var generator = new StubGenerator();
            var store = new StubSettingsStore { Settings = new AiSettings() };
            var notifier = new RecordingNotifier();
            var vm = BuildViewModel(generator, store, notifier);

            vm.GenerateCommitMessageCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(generator.CallCount, Is.Zero);
                Assert.That(notifier.Infos, Has.Some.Contains("도구 > 옵션"));
            });
        }
    }
}
```

> **준비 절차가 기존 픽스처와 어긋나면 그쪽을 기준으로 맞춘다.** `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`의 `[SetUp]`과 `NewConnectedViewModel`이 원본이다. Moq 설정이 하나라도 빠지면 `ConnectCommand`가 다른 경로로 새고, 그러면 이 픽스처의 실패가 AI 코드 때문인지 준비 때문인지 구분되지 않는다. `RecordingNotifier`는 같은 네임스페이스의 `TestDoubles.cs`에 이미 있다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~AiCommitMessageTests"`
Expected: FAIL — `GenerateCommitMessageCommand`가 없다.

- [ ] **Step 3: ViewModel에 필드와 명령을 더한다**

`ViewChangesViewModel`의 필드 선언부에 더한다.

```csharp
        private readonly IAiCommitMessageGenerator? _aiGenerator;
        private readonly IAiSettingsStore _aiSettingsStore;
```

생성자 매개변수 끝에 더한다(기존 인자는 그대로 둔다).

```csharp
            IAiCommitMessageGenerator? aiGenerator = null,
            IAiSettingsStore? aiSettingsStore = null)
```

생성자 본문에서 배선한다.

```csharp
            _aiGenerator = aiGenerator;
            _aiSettingsStore = aiSettingsStore ?? new AiSettingsStore();
            GenerateCommitMessageCommand = new RelayCommand(GenerateCommitMessage, CanGenerateCommitMessage);
```

`CommitCommand` 선언 옆에 더한다.

```csharp
        public ICommand GenerateCommitMessageCommand { get; }
```

`RaiseActionCanExecuteChanged`가 명령들을 갱신하는 자리에 `GenerateCommitMessageCommand`도 더한다(기존 구현이 어떤 목록을 도는지 보고 같은 방식으로 넣는다).

- [ ] **Step 4: 명령 본문을 쓴다**

`Commit` 관련 메서드 아래(`// ---------- Commit ----------` 구역 끝)에 더한다.

```csharp
        // ---------- AI 커밋 메시지 ----------

        /// <summary>
        /// 설정 유무는 조건이 아니다. 설정이 없다고 버튼을 잠그면 사용자는 이유를 알 수 없다 —
        /// 눌렀을 때 어디서 설정하는지 알려 주는 편이 낫다.
        /// </summary>
        private bool CanGenerateCommitMessage()
        {
            if (IsBlocked) return false;

            return HasContext
                && IsMapped
                && IsInitialized
                && !IsBusy
                && Changes.Any(c => c.IsSelected)
                && MappingPolicy.IsAllowed(Mode, DbvcOperation.Commit);
        }

        private void GenerateCommitMessage()
        {
            if (!CanGenerateCommitMessage() || _aiGenerator == null) return;

            var settings = _aiSettingsStore.Load();
            if (!settings.IsConfigured)
            {
                _notifier.ShowInfo(
                    "AI 커밋 메시지",
                    "AI 설정이 없습니다.\n도구 > 옵션 > DBVC > AI 커밋 메시지에서 프로바이더 주소와 모델을 설정하세요.");
                return;
            }

            // 손으로 적던 문장이 클릭 한 번에 사라지면 안 된다.
            if (!string.IsNullOrWhiteSpace(CommitMessage)
                && !_notifier.Confirm("AI 커밋 메시지", "입력한 커밋 메시지를 AI가 만든 문장으로 바꿉니다. 계속하시겠습니까?"))
            {
                return;
            }

            if (AiConsent.NeedsConsent(settings))
            {
                var host = AiConsent.HostOf(settings.BaseUrl);
                if (!_notifier.Confirm(
                        "AI 커밋 메시지",
                        $"선택한 변경의 SQL diff가 {host}로 전송됩니다. 계속하시겠습니까?"))
                {
                    return;
                }

                // 동의한 목적지를 남긴다. 주소가 바뀌면 다시 묻는다.
                settings.ConsentedHost = host;
                _aiSettingsStore.Save(settings);
            }

            var selectedPaths = Changes
                .Where(c => c.IsSelected && !string.IsNullOrWhiteSpace(c.RelativePath))
                .Select(c => c.RelativePath!)
                .ToList();

            var server = ServerName!;
            var database = DatabaseName!;

            IsBusy = true;
            ProgressText = "AI가 커밋 메시지를 만드는 중...";
            _scheduler.Run<string>(
                () => _aiGenerator.Generate(server, database, selectedPaths, CancellationToken.None),
                message =>
                {
                    ProgressText = null;
                    IsBusy = false;
                    CommitMessage = message;
                    RaiseActionCanExecuteChanged();
                },
                ex =>
                {
                    ProgressText = null;
                    IsBusy = false;
                    // 실패해도 CommitMessage는 건드리지 않는다. 빈칸으로 만들면 적던 것까지 잃는다.
                    _notifier.ShowError("AI 커밋 메시지", ex.Message);
                    RaiseActionCanExecuteChanged();
                });
        }
```

> `ProgressText`·`IsBusy`의 실제 이름과 설정 방식은 기존 `Commit`/`Refresh` 구현을 보고 그대로 맞춘다(`Busy` 상태 객체를 거칠 수 있다).

- [ ] **Step 5: XAML에 버튼을 더한다**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml`에서 커밋 메시지 TextBox와 Commit 버튼 사이에 넣는다.

```xml
                    <Button Content="AI 생성" Command="{Binding GenerateCommitMessageCommand}" Width="70" Margin="0,0,10,4"
                            ToolTip="선택한 변경의 내용을 AI에게 보내 커밋 메시지 초안을 만듭니다.&#10;커밋하지는 않습니다 - 내용을 확인하고 고친 뒤 Commit을 누르세요." />
```

- [ ] **Step 6: `DbvcServices`에 배선한다**

```csharp
        public IAiSettingsStore AiSettingsStore { get; }

        /// <summary>
        /// 커밋 메시지 초안 생성기. 설정 저장소를 화면과 공유해야 한다 —
        /// 따로 만들면 옵션 화면에서 저장한 값이 생성기에 보이지 않는다.
        /// </summary>
        public IAiCommitMessageGenerator AiCommitMessageGenerator { get; }
```

각 생성자에서 채운다.

```csharp
            AiSettingsStore = new AiSettingsStore();
            AiCommitMessageGenerator = new CommitMessageGenerator(GitManager, AiSettingsStore, new OpenAiCompatibleClient());
```

`CreateViewChangesViewModel`의 인자에 더한다.

```csharp
                aiGenerator: AiCommitMessageGenerator,
                aiSettingsStore: AiSettingsStore);
```

- [ ] **Step 7: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~AiCommitMessageTests"`
Expected: PASS (6개)

- [ ] **Step 8: 전체 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: PASS — 기존 테스트가 깨지지 않았는지 본다(생성자 인자를 더했으므로 호출부가 영향을 받는다).

- [ ] **Step 9: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml src/DBVC.Vsix/DbvcServices.cs tests/DBVC.Vsix.Tests/ViewModels/AiCommitMessageTests.cs
git commit -m "feat(vsix): 커밋 메시지 초안을 만드는 AI 생성 버튼을 더한다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: 옵션 페이지

**Files:**
- Create: `src/DBVC.Vsix/UI/AiOptionsControl.xaml`, `src/DBVC.Vsix/UI/AiOptionsControl.xaml.cs`, `src/DBVC.Vsix/UI/AiOptionPage.cs`
- Modify: `src/DBVC.Vsix/DbvcPackage.cs`

**Interfaces:**
- Consumes: `IAiSettingsStore`, `AiSettings`, `IChatCompletionClient`, `AiRequestException`(Task 2·5), `DbvcServices.AiSettingsStore`(Task 8)
- Produces: `AiOptionPage : UIElementDialogPage` — 도구 > 옵션 > DBVC > AI 커밋 메시지

- [ ] **Step 1: 옵션 화면(WPF)을 쓴다**

`src/DBVC.Vsix/UI/AiOptionsControl.xaml`:

```xml
<UserControl x:Class="DBVC.Vsix.UI.AiOptionsControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="8">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto" />
            <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Grid.Column="0" Text="프로바이더 주소" Margin="0,0,8,6" VerticalAlignment="Center" />
        <TextBox Grid.Row="0" Grid.Column="1" x:Name="BaseUrlBox" Margin="0,0,0,6"
                 ToolTip="OpenAI 호환 엔드포인트의 기준 주소입니다. 예: https://api.openai.com/v1" />

        <TextBlock Grid.Row="1" Grid.Column="0" Text="모델" Margin="0,0,8,6" VerticalAlignment="Center" />
        <TextBox Grid.Row="1" Grid.Column="1" x:Name="ModelBox" Margin="0,0,0,6" />

        <TextBlock Grid.Row="2" Grid.Column="0" Text="API 키" Margin="0,0,8,6" VerticalAlignment="Center" />
        <PasswordBox Grid.Row="2" Grid.Column="1" x:Name="ApiKeyBox" Margin="0,0,0,6"
                     ToolTip="인증이 필요 없는 사내 서버라면 비워 둡니다." />

        <TextBlock Grid.Row="3" Grid.Column="0" Text="응답 대기(초)" Margin="0,0,8,6" VerticalAlignment="Center" />
        <TextBox Grid.Row="3" Grid.Column="1" x:Name="TimeoutBox" Margin="0,0,0,6" Width="80" HorizontalAlignment="Left" />

        <TextBlock Grid.Row="4" Grid.Column="0" Text="보낼 diff 최대 줄 수" Margin="0,0,8,6" VerticalAlignment="Center" />
        <TextBox Grid.Row="4" Grid.Column="1" x:Name="MaxDiffLinesBox" Margin="0,0,0,6" Width="80" HorizontalAlignment="Left"
                 ToolTip="이 줄 수를 넘으면 객체별로 잘라서 보냅니다. 변경 객체 목록은 그대로 보냅니다." />

        <StackPanel Grid.Row="5" Grid.Column="1" Orientation="Horizontal" Margin="0,4,0,6">
            <Button x:Name="TestButton" Content="연결 테스트" Width="100" Click="OnTestConnection" />
            <TextBlock x:Name="TestResultText" Margin="10,0,0,0" VerticalAlignment="Center" TextWrapping="Wrap" />
        </StackPanel>

        <TextBlock Grid.Row="6" Grid.Column="1" TextWrapping="Wrap" Margin="0,8,0,0" Opacity="0.8"
                   Text="AI 생성을 누르면 선택한 변경의 SQL diff가 위 주소로 전송됩니다. 주소를 바꾸면 전송 전에 다시 확인합니다." />
    </Grid>
</UserControl>
```

- [ ] **Step 2: 코드비하인드를 쓴다**

`src/DBVC.Vsix/UI/AiOptionsControl.xaml.cs`:

```csharp
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 도구 > 옵션 > DBVC > AI 커밋 메시지의 화면.
    ///
    /// API 키를 PropertyGrid에 노출하지 않으려고 UIElementDialogPage를 쓴다 —
    /// 기본 DialogPage는 속성을 그대로 그려 키가 평문으로 보인다.
    /// </summary>
    public partial class AiOptionsControl : UserControl
    {
        public AiOptionsControl()
        {
            InitializeComponent();
        }

        public void LoadFrom(AiSettings settings)
        {
            BaseUrlBox.Text = settings.BaseUrl;
            ModelBox.Text = settings.Model;
            ApiKeyBox.Password = settings.ApiKey;
            TimeoutBox.Text = settings.TimeoutSeconds.ToString();
            MaxDiffLinesBox.Text = settings.MaxDiffLines.ToString();
            _consentedHost = settings.ConsentedHost;
        }

        private string? _consentedHost;

        public AiSettings ToSettings()
        {
            return new AiSettings
            {
                BaseUrl = BaseUrlBox.Text?.Trim() ?? string.Empty,
                Model = ModelBox.Text?.Trim() ?? string.Empty,
                ApiKey = ApiKeyBox.Password ?? string.Empty,
                TimeoutSeconds = ParsePositive(TimeoutBox.Text, 30),
                MaxDiffLines = ParsePositive(MaxDiffLinesBox.Text, 400),
                // 동의 기록은 화면이 만들지 않는다. 주소가 바뀌면 AiConsent가 알아서 다시 묻는다.
                ConsentedHost = _consentedHost,
            };
        }

        private static int ParsePositive(string? text, int fallback) =>
            int.TryParse(text, out var value) && value > 0 ? value : fallback;

        /// <summary>
        /// 세 값이 모두 맞아야 동작하는데, 틀렸다는 것을 커밋하려던 순간에 처음 알면 안 된다.
        /// </summary>
        private void OnTestConnection(object sender, RoutedEventArgs e)
        {
            var settings = ToSettings();
            if (!settings.IsConfigured)
            {
                TestResultText.Text = "프로바이더 주소와 모델을 먼저 입력하세요.";
                return;
            }

            TestButton.IsEnabled = false;
            TestResultText.Text = "확인 중...";

            var client = new OpenAiCompatibleClient();
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await client.CompleteAsync(settings, "ping", "ping", CancellationToken.None);
                    return "연결에 성공했습니다.";
                }
                catch (AiRequestException ex)
                {
                    return ex.Message;
                }
                catch (Exception ex)
                {
                    return "연결에 실패했습니다.\n\n" + ex.Message;
                }
            }).ContinueWith(task =>
            {
                TestResultText.Text = task.Result;
                TestButton.IsEnabled = true;
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
```

- [ ] **Step 3: 옵션 페이지를 쓴다**

`src/DBVC.Vsix/UI/AiOptionPage.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using DBVC.Core;
using Microsoft.VisualStudio.Shell;

namespace DBVC.Vsix.UI
{
    [Guid("6d1b4a72-0f35-4b8e-9c2a-51d7e0a34c18")]
    public class AiOptionPage : UIElementDialogPage
    {
        private AiOptionsControl? _control;

        private IAiSettingsStore Store => DbvcServices.Default.AiSettingsStore;

        protected override UIElement Child => _control ??= new AiOptionsControl();

        /// <summary>
        /// 기본 구현은 속성을 VS 설정 저장소(레지스트리)에 <b>평문으로</b> 남긴다.
        /// 이 두 메서드를 재정의해 저장을 AiSettingsStore 하나로 몰지 않으면
        /// DPAPI 암호화가 통째로 무의미해진다. base를 부르지 않는 것이 요점이다.
        /// </summary>
        public override void LoadSettingsFromStorage()
        {
            ((AiOptionsControl)Child).LoadFrom(Store.Load());
        }

        public override void SaveSettingsToStorage()
        {
            Store.Save(((AiOptionsControl)Child).ToSettings());
        }

        /// <summary>대화상자를 취소로 닫으면 저장하지 않는다.</summary>
        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
        }
    }
}
```

- [ ] **Step 4: 패키지에 등록한다**

`src/DBVC.Vsix/DbvcPackage.cs`의 특성 목록에 더한다.

```csharp
    [ProvideOptionPage(typeof(UI.AiOptionPage), "DBVC", "AI 커밋 메시지", 0, 0, true)]
```

- [ ] **Step 5: 빌드한다**

Run: `dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release`
Expected: 성공. 이어서 산출물이 실제로 나왔는지 확인한다 — **빌드 성공은 `.vsix` 생성을 뜻하지 않는다.**

Run: `dir src\DBVC.Vsix\bin\Release\net48\*.vsix` (PowerShell)
Expected: `.vsix` 파일 하나.

- [ ] **Step 6: 테스트 전체를 돌린다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: PASS

- [ ] **Step 7: SSMS 21에서 직접 확인한다 (CI가 대신해 주지 못한다)**

계획서가 통과를 선언할 수 없는 유일한 단계다. 다음을 눈으로 본다.

1. `.vsix`를 설치하고 SSMS 21을 연다.
2. **도구 > 옵션**에 `DBVC > AI 커밋 메시지`가 보인다.
3. 주소·모델·키를 넣고 **확인**으로 닫는다. `%APPDATA%\DBVC\ai-settings.json`이 생기고, **그 파일을 열었을 때 키 원문이 보이지 않는다.**
4. 다시 옵션을 열면 값이 그대로 있다.
5. `연결 테스트`가 성공 또는 한국어 사유를 낸다.
6. 레지스트리에 평문이 남지 않았는지 본다 — VS 설정 저장소에 `AiOptionPage`의 속성이 기록되지 않아야 한다.

- [ ] **Step 8: 커밋**

```bash
git add src/DBVC.Vsix/UI/AiOptionsControl.xaml src/DBVC.Vsix/UI/AiOptionsControl.xaml.cs src/DBVC.Vsix/UI/AiOptionPage.cs src/DBVC.Vsix/DbvcPackage.cs
git commit -m "feat(vsix): 도구 옵션에 AI 커밋 메시지 설정 페이지를 더한다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: 문서와 버전

**Files:**
- Modify: `README.md`, `docs/setup-checklist.md`, `src/DBVC.Vsix/source.extension.vsixmanifest:4`

**Interfaces:**
- Consumes: 앞의 모든 태스크
- Produces: 없음(문서)

- [ ] **Step 1: `README.md`에 기능을 적는다**

기능 목록과 사용 절차가 있는 자리를 찾아 다음을 더한다. 기존 문체(한국어 평서문)를 따른다.

- `AI 생성` 버튼이 선택한 변경의 diff로 커밋 메시지 초안을 만든다는 것
- 설정 위치: 도구 > 옵션 > DBVC > AI 커밋 메시지 (프로바이더 주소·모델·API 키)
- OpenAI 호환 엔드포인트면 사내 서버든 외부 API든 쓸 수 있다는 것
- **선택한 변경의 SQL diff가 그 주소로 전송된다는 것**과, 주소가 바뀌면 전송 전에 확인을 받는다는 것
- API 키는 `%APPDATA%\DBVC\ai-settings.json`에 DPAPI로 암호화되어 저장되며 같은 PC의 다른 계정은 읽지 못한다는 것

- [ ] **Step 2: `docs/setup-checklist.md`에 설정 절차를 더한다**

기존 항목 형식에 맞춰, 선택 단계로 적는다(AI 설정 없이도 DBVC의 나머지는 전부 동작한다).

- [ ] **Step 3: 매니페스트 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest:4`의 `Version="0.6.0"`을 `Version="0.7.0"`으로 바꾼다. 사용자 눈에 보이는 동작이 늘었으므로 마이너를 올린다.

- [ ] **Step 4: 전체 빌드와 테스트를 돌린다**

```bash
dotnet build DBVC.slnx
dotnet test tests/DBVC.Core.Tests -f net10.0
dotnet test tests/DBVC.Vsix.Tests -f net48
```

Expected: 전부 PASS.

- [ ] **Step 5: 커밋**

```bash
git add README.md docs/setup-checklist.md src/DBVC.Vsix/source.extension.vsixmanifest
git commit -m "docs: AI 커밋 메시지 기능과 설정 절차를 적는다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## 완료 조건

- `dotnet test tests/DBVC.Core.Tests -f net10.0`과 `dotnet test tests/DBVC.Vsix.Tests -f net48`이 통과한다.
- `ai-settings.json`에 API 키 원문이 없다(Task 2의 단언 + Task 9 Step 7의 육안 확인).
- SSMS 21에서 도구 > 옵션 > DBVC > AI 커밋 메시지가 열리고 값이 왕복한다.
- SSMS 21에서 `AI 생성`을 눌러 커밋 메시지가 채워지고, 실패해도 도구 창이 살아 있다.
