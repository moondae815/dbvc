using System.Text;

namespace DBVC.Baseline
{
    public enum BaselineVerdict
    {
        /// <summary>추출·검증 모두 깨끗하다.</summary>
        Clean,

        /// <summary>스크립팅에 실패한 객체가 있다. 기준선이 불완전하다.</summary>
        ExtractionFailed,

        /// <summary>검증에서 차이가 나왔다. 이 기준선은 믿을 수 없다.</summary>
        VerificationFailed,

        /// <summary>사전 점검이나 인자에서 멈췄다. 아무것도 쓰지 않았다.</summary>
        PreflightFailed
    }

    public sealed class BaselineReport
    {
        public BaselineVerdict Verdict { get; set; }
        public int ExtractedCount { get; set; }
        public List<string> ExtractionFailures { get; } = new();
        public int ComparedCount { get; set; }

        /// <summary>검증에서 나온 차이·미판정 객체를 사람이 읽을 한 줄씩.</summary>
        public List<string> VerificationFindings { get; } = new();

        public bool RepositoryScanCompleted { get; set; } = true;

        /// <summary>사전 점검 실패 등, 판정 이전에 멈춘 사유.</summary>
        public string? StopReason { get; set; }

        public int ExitCode => Verdict switch
        {
            BaselineVerdict.Clean => 0,
            BaselineVerdict.ExtractionFailed => 1,
            BaselineVerdict.VerificationFailed => 2,
            _ => 3
        };

        public string Render()
        {
            var sb = new StringBuilder();

            if (StopReason != null)
            {
                sb.AppendLine(StopReason);
                return sb.ToString();
            }

            sb.AppendLine($"추출한 객체: {ExtractedCount}개");

            if (ExtractionFailures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"스크립팅에 실패한 객체 {ExtractionFailures.Count}개 —");
                sb.AppendLine("대개 VIEW DEFINITION 권한이 없거나 암호화된 모듈입니다.");
                foreach (var name in ExtractionFailures)
                {
                    sb.AppendLine($"  - {name}");
                }
                sb.AppendLine();
                sb.AppendLine("기준선이 불완전합니다. 커밋하지 마세요 — 빠진 객체는 나중에 " +
                              "\"브랜치에만 있음\"으로 떠서 배포 스크립트에 CREATE가 들어갑니다.");
                return sb.ToString();
            }

            if (Verdict == BaselineVerdict.VerificationFailed)
            {
                sb.AppendLine($"검증한 객체: {ComparedCount}개");
                sb.AppendLine();
                sb.AppendLine("검증에서 차이가 나왔습니다 —");
                foreach (var finding in VerificationFindings)
                {
                    sb.AppendLine($"  - {finding}");
                }
                if (!RepositoryScanCompleted)
                {
                    sb.AppendLine("  - 저장소 스캔이 끝까지 돌지 못했습니다.");
                }
                sb.AppendLine();
                sb.AppendLine("이 기준선은 믿을 수 없습니다. 커밋하지 마세요. " +
                              "실행 중에 운영이 바뀌었을 수 있으니 다시 돌려 보세요.");
                return sb.ToString();
            }

            sb.AppendLine($"검증한 객체: {ComparedCount}개, 차이 없음");
            sb.AppendLine();
            sb.AppendLine("기준선이 만들어졌습니다. git status로 확인한 뒤 커밋해도 됩니다.");
            return sb.ToString();
        }
    }
}
