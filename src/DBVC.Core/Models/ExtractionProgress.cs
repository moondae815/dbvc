namespace DBVC.Core.Models
{
    /// <summary>
    /// 추출이 어디까지 진행됐는지. 최초 온보딩은 객체 수에 비례해 길어지므로
    /// (실측: 사용자 객체 200개 DB의 전체 추출 186초) 화면이 말할 것이 있어야 한다.
    /// </summary>
    public sealed class ExtractionProgress
    {
        public ExtractionProgress(int completed, int total, string? currentObject)
        {
            Completed = completed;
            Total = total;
            CurrentObject = currentObject;
        }

        /// <summary>처리를 마친 객체 수. 실패한 객체도 포함한다 — 진행이 멈춘 것처럼 보이면 안 된다.</summary>
        public int Completed { get; }

        /// <summary>이번 추출의 대상 객체 수.</summary>
        public int Total { get; }

        /// <summary>방금 처리한 객체의 스키마 한정 이름.</summary>
        public string? CurrentObject { get; }
    }
}
