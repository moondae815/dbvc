using System;
using DBVC.Core.Models;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 추출 보고를 그 자리에서 전달한다. <see cref="Progress{T}"/>는 생성된 스레드의
    /// SynchronizationContext로 넘기는데, 백그라운드 스레드에는 그것이 없어 보고가
    /// 스레드 풀로 흩어지고 순서가 뒤집힌다.
    ///
    /// ViewChangesViewModel과 DeploymentViewModel이 함께 쓴다 — 둘로 나뉘면 한쪽만 고쳐진다.
    /// </summary>
    internal sealed class ExtractionProgressRelay : IProgress<ExtractionProgress>
    {
        private readonly Action<ExtractionProgress> _onReport;
        public ExtractionProgressRelay(Action<ExtractionProgress> onReport) { _onReport = onReport; }
        public void Report(ExtractionProgress value) => _onReport(value);
    }
}
