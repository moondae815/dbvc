using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Baseline
{
    /// <summary>
    /// 기준선을 만드는 순서를 지휘한다.
    ///
    /// 두 인터페이스만 알기 때문에 SQL Server 없이 전부 테스트된다. 운영 DB에 쓰는 경로가
    /// 하나도 없다 — IStateTracker를 참조하지 않으므로 InitializeDatabase에 닿을 수 없다.
    /// </summary>
    public sealed class BaselineRunner
    {
        private readonly IConfigManager _configManager;
        private readonly ISmoManager _smoManager;

        public BaselineRunner(IConfigManager configManager, ISmoManager smoManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _smoManager = smoManager ?? throw new ArgumentNullException(nameof(smoManager));
        }

        public BaselineReport Run(
            BaselineOptions options,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var report = new BaselineReport();

            // 추출은 Write에서만 허용된다(MappingPolicy).
            SetMode(options, MappingMode.Write);

            var script = _smoManager.ScriptObjectsDetailed(
                options.Server, options.Database, null, progress, cancellationToken);

            if (script == null)
            {
                report.Verdict = BaselineVerdict.ExtractionFailed;
                report.StopReason = "추출이 시작되지 못했습니다. 접속과 --repo 경로를 확인하세요.";
                return report;
            }

            report.ExtractedCount = script.SucceededCount;
            report.ExtractionFailures.AddRange(script.FailedObjects);

            if (script.HasFailures)
            {
                // 이미 불완전한 기준선을 검증해도 새로 알 것이 없다. 운영을 두 번 읽지 않는다.
                report.Verdict = BaselineVerdict.ExtractionFailed;
                return report;
            }

            // 차이 검사는 Write에서 금지다. 같은 대상을 Audit으로 덮어쓴다.
            SetMode(options, MappingMode.Audit);

            var comparison = _smoManager.CompareWithRepository(
                options.Server, options.Database, progress, cancellationToken);

            if (comparison == null)
            {
                report.Verdict = BaselineVerdict.VerificationFailed;
                report.StopReason = "검증이 시작되지 못했습니다. 이 기준선은 믿을 수 없습니다.";
                return report;
            }

            report.ComparedCount = comparison.ComparedCount;
            report.RepositoryScanCompleted = comparison.RepositoryScanCompleted;

            foreach (var difference in comparison.Differences)
            {
                report.VerificationFindings.Add($"{difference.QualifiedName} — {Describe(difference.State)}");
            }
            foreach (var failed in comparison.FailedObjects)
            {
                report.VerificationFindings.Add($"{failed} — 판정하지 못함");
            }

            // IsInSync만 보면 안 된다. 판정하지 못한 객체와 다 읽지 못한 저장소는
            // Differences에 들어오지 않는다(ComparisonResult의 주석).
            bool verified = comparison.IsInSync
                && comparison.FailedObjects.Count == 0
                && comparison.RepositoryScanCompleted;

            report.Verdict = verified ? BaselineVerdict.Clean : BaselineVerdict.VerificationFailed;
            return report;
        }

        private void SetMode(BaselineOptions options, MappingMode mode)
        {
            _configManager.AddMapping(new MappingConfig
            {
                ServerName = options.Server,
                DatabaseName = options.Database,
                GitPath = options.RepositoryPath,
                Mode = mode
            });
        }

        private static string Describe(ObjectDiffState state) => state switch
        {
            ObjectDiffState.Modified => "수정됨",
            ObjectDiffState.MissingInBranch => "DB에만 있음",
            _ => "브랜치에만 있음"
        };
    }
}
