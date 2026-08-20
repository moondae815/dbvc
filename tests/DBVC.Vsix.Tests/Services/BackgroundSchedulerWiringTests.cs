using System;
using System.Threading;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.Tests.Services
{
    /// <summary>
    /// ViewModel의 기본 스케줄러는 인라인이다 — 단위 테스트와 셸 밖 실행이 그 경로다.
    /// 그래서 "실제 도구 창이 UI 스레드를 비우는 구현을 받는가"는 ViewModel 테스트로는
    /// 증명되지 않는다. 배선이 끊기면 SSMS는 예전처럼 그대로 멈추고, 단위 테스트는 전부 통과한다.
    /// 이 픽스처가 그 틈을 막는다.
    /// </summary>
    [TestFixture]
    public class BackgroundSchedulerWiringTests
    {
        [Test]
        public void DbvcServices_DoesNotUseTheInlineScheduler_ByDefault()
        {
            Assert.That(new DbvcServices().BackgroundScheduler, Is.Not.InstanceOf<InlineBackgroundScheduler>(),
                "인라인 스케줄러를 쓰면 새로고침이 다시 UI 스레드를 붙잡아 SSMS가 멈춘다");
        }

        [Test]
        public void CreateViewChangesViewModel_PassesTheContainersSchedulerToTheViewModel()
        {
            var scheduler = new RecordingScheduler();
            var services = NewServices(scheduler);

            var ssms = new Mock<ISsmsConnectionSource>();
            ssms.Setup(s => s.TryGetCurrent())
                .Returns(new SsmsConnectionInfo("S", "D", SqlAuthMode.Windows, null, null, null));

            var vm = services.CreateViewChangesViewModel(ssmsConnectionSource: ssms.Object);
            vm.ConnectCommand.Execute(null);

            Assert.That(scheduler.RunCount, Is.GreaterThan(0),
                "도구 창의 ViewModel이 컨테이너의 스케줄러를 받지 못하면 무거운 일이 UI 스레드에 남는다");
        }

        private static DbvcServices NewServices(IBackgroundScheduler scheduler)
        {
            var config = new Mock<IConfigManager>();
            config.Setup(c => c.TryGetMapping(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new MappingConfig { ServerName = "S", DatabaseName = "D", GitPath = @"C:\repo" });

            var stateTracker = new Mock<IStateTracker>();
            stateTracker.Setup(s => s.TestConnection(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
            stateTracker.Setup(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>())).Returns(StateTracker.RequiredSchemaVersion);
            stateTracker.Setup(s => s.RefreshState(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            stateTracker.Setup(s => s.GetPendingChanges(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new List<ChangeRecord>());

            var smo = new Mock<ISmoManager>();
            smo.Setup(s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(new ScriptResult());

            return new DbvcServices(
                config.Object, new Mock<IGitManager>().Object, smo.Object, stateTracker.Object,
                credentialStore: null, backgroundScheduler: scheduler);
        }

        /// <summary>넘겨받은 작업을 인라인으로 실행하되 호출 횟수를 센다.</summary>
        private sealed class RecordingScheduler : IBackgroundScheduler
        {
            public int RunCount { get; private set; }

            public void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed)
            {
                RunCount++;

                T value;
                try { value = work(); }
                catch (Exception ex) { onFailed(ex); return; }
                onSucceeded(value);
            }

            public void Post(Action action) => action();
        }
    }
}
