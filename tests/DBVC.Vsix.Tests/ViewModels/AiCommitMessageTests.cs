using System;
using System.Collections.Generic;
using System.IO;
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
            // ConsentedHost는 AiConsent.DestinationOf가 이 BaseUrl에 대해 돌려주는 값과 같아야
            // "이미 동의한" 상태를 나타낸다 - 스킴을 뺀 맨 호스트를 넣으면 매번 어긋나 불필요한
            // 동의 확인이 다시 뜬다.
            public AiSettings Settings { get; set; } = new AiSettings
            {
                BaseUrl = "https://llm.example.com/v1",
                Model = "m",
                ConsentedHost = "https://llm.example.com",
            };

            public Exception? SaveThrows { get; set; }

            public AiSettings Load() => Settings;

            public void Save(AiSettings settings)
            {
                if (SaveThrows != null) throw SaveThrows;
                Settings = settings;
            }
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
            Assert.That(store.Settings.ConsentedHost, Is.EqualTo("https://api.openai.com"));
        }

        [Test]
        public void GenerateCommitMessage_PassesOnlySelectedPaths_ToGenerator()
        {
            // LastPaths는 이전부터 있었지만 아무도 확인하지 않았다 — 선택하지 않은 객체까지
            // generator로 넘어가면 그 SQL이 외부 프로바이더로 나간다. 이 인자가 그 범위를
            // 지키는 유일한 자리이므로, 선택하지 않은 변경을 하나 더 추가해 실제로 걸러지는지
            // 본다.
            var generator = new StubGenerator();
            var notifier = new RecordingNotifier();
            var vm = BuildViewModel(generator, new StubSettingsStore(), notifier);
            vm.Changes.Add(new ChangeItemViewModel
            {
                ObjectName = "dbo.v_Secret",
                ObjectType = "VIEW",
                State = "Modified",
                RelativePath = "dbo/Views/v_Secret.sql",
                IsSelected = false,
            });

            vm.GenerateCommitMessageCommand.Execute(null);

            Assert.That(generator.LastPaths, Is.EqualTo(new List<string> { ChangedPath }));
        }

        [Test]
        public void GenerateCommitMessage_StillGenerates_WhenConsentSaveFails()
        {
            // AiSettingsStore.Save는 IO/권한 오류를 일부러 삼키지 않는다. RelayCommand.Execute에는
            // try/catch가 없으므로, 여기서 잡지 않으면 방금 사용자가 동의 클릭을 누른 순간
            // 예외가 UI 스레드로 그대로 샌다. 저장에 실패해도 이미 승인받은 이번 생성은
            // 계속돼야 한다.
            var generator = new StubGenerator();
            var store = new StubSettingsStore
            {
                Settings = new AiSettings { BaseUrl = "https://api.openai.com/v1", Model = "m", ConsentedHost = null },
                SaveThrows = new IOException("디스크가 가득 찼습니다."),
            };
            var notifier = new RecordingNotifier { ConfirmResult = true };
            var vm = BuildViewModel(generator, store, notifier);

            vm.GenerateCommitMessageCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(generator.CallCount, Is.EqualTo(1));
                Assert.That(vm.CommitMessage, Is.EqualTo("feat: 주문 뷰를 더한다"));
                Assert.That(notifier.Errors, Has.Some.Contains("동의"));
            });
        }

        [Test]
        public void GenerateCommitMessage_ShowsGuidance_WhenSettingsMissing()
        {
            // 버튼을 잠그지 않는 대신, 누르면 어디서 설정하는지 알려 준다.
            //
            // 주소·모델을 명시적으로 비운다. AiSettings의 기본값이 채워져 있으므로
            // new AiSettings()는 더 이상 "설정이 없는 상태"가 아니다 — 이 경로가 살아 있는지
            // 보려면 사용자가 일부러 비운 상태를 만들어야 한다.
            var generator = new StubGenerator();
            var store = new StubSettingsStore
            {
                Settings = new AiSettings { BaseUrl = string.Empty, Model = string.Empty },
            };
            var notifier = new RecordingNotifier();
            var vm = BuildViewModel(generator, store, notifier);

            vm.GenerateCommitMessageCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(generator.CallCount, Is.Zero);
                Assert.That(notifier.Infos, Has.Some.Contains("도구 > 옵션"));
            });
        }

        [Test]
        public void GenerateCommitMessage_ShowsGuidance_WhenGeneratorNotConfigured()
        {
            // aiGenerator가 null이면(조립 루트에서 만들지 못한 경우) 버튼은 여전히 눌리지만,
            // 조용히 아무 일도 하지 않으면 안 된다 — CanGenerateCommitMessage 바로 위 주석이
            // "잠긴 버튼은 이유를 말하지 못한다"고 적은 것과 같은 원칙이 여기도 적용된다.
            var notifier = new RecordingNotifier();
            var vm = new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, notifier,
                saveDialog: null,
                cleaner: _cleaner.Object,
                connectDialog: null,
                credentialStore: _credentials.Object,
                ssmsConnectionSource: _ssms.Object,
                scheduler: new InlineBackgroundScheduler(),
                aiGenerator: null,
                aiSettingsStore: new StubSettingsStore());
            vm.ConnectCommand.Execute(null);
            vm.Changes.Add(new ChangeItemViewModel
            {
                ObjectName = "dbo.v_Order",
                ObjectType = "VIEW",
                State = "Added",
                RelativePath = ChangedPath,
                IsSelected = true,
            });

            vm.GenerateCommitMessageCommand.Execute(null);

            Assert.That(notifier.Infos, Has.Some.Contains("도구 > 옵션"));
        }
    }
}
