namespace DBVC.Vsix.Services
{
    /// <summary>사용자가 입력한 커밋 작성자 신원.</summary>
    public sealed class CommitIdentityInput
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    /// <summary>
    /// 커밋 작성자를 사용자에게 묻는다. ViewModel이 WPF 창에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface ICommitIdentityDialog
    {
        /// <summary>사용자가 취소하면 <c>null</c>.</summary>
        CommitIdentityInput? Prompt(string suggestedName, string suggestedEmail);
    }
}
