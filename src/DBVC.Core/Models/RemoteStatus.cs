namespace DBVC.Core.Models
{
    /// <summary>
    /// 원격을 읽어 본 결과. Fetch는 참조만 갱신하고 작업 트리를 건드리지 않으므로
    /// 이 값을 얻는 데 부수효과가 없다.
    /// </summary>
    public sealed class RemoteStatus
    {
        public RemoteStatus(int aheadBy, int behindBy)
        {
            AheadBy = aheadBy;
            BehindBy = behindBy;
        }

        /// <summary>원격에 없는 로컬 커밋 수. Push할 것.</summary>
        public int AheadBy { get; }

        /// <summary>로컬에 없는 원격 커밋 수. Pull할 것.</summary>
        public int BehindBy { get; }
    }
}
