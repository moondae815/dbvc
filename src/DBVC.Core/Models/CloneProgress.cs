namespace DBVC.Core.Models
{
    /// <summary>
    /// clone의 단계. 화면이 이것을 알아야 하는 이유는 진행률 문구가 아니라 취소 버튼이다 —
    /// libgit2의 CheckoutProgressHandler는 void라 펼치는 단계는 끊을 수 없다.
    /// </summary>
    public enum ClonePhase
    {
        /// <summary>원격에서 객체를 받는 중. 취소가 실제로 걸리는 유일한 단계다.</summary>
        Transferring = 0,

        /// <summary>받은 것을 작업 트리에 펼치는 중.</summary>
        CheckingOut = 1
    }

    /// <summary>clone이 어느 단계에서 어디까지 왔는지.</summary>
    public sealed class CloneProgress
    {
        public CloneProgress(ClonePhase phase, int completed, int total)
        {
            Phase = phase;
            Completed = completed;
            Total = total;
        }

        public ClonePhase Phase { get; }

        /// <summary>받은 객체 수 또는 펼친 단계 수.</summary>
        public int Completed { get; }

        /// <summary>전체 수. 원격이 알려주기 전에는 0일 수 있다.</summary>
        public int Total { get; }
    }
}
