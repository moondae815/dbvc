using System;

namespace DBVC.Core
{
    /// <summary>
    /// 병합이 실패했고, 병합 전으로 되돌리는 것마저 실패했다. 메시지는 원래 실패를 먼저 싣는다 -
    /// 되돌리기 실패로 바꿔 던지면 병합이 왜 실패했는지가 사라진다. 원래 예외는
    /// <see cref="Exception.InnerException"/>에, 되돌리기 실패는 <see cref="RollbackFailure"/>에 있다.
    /// </summary>
    public class GitMergeRollbackException : Exception
    {
        public GitMergeRollbackException(Exception cause, Exception rollbackFailure, string? headBeforeSha)
            : base(BuildMessage(cause, rollbackFailure, headBeforeSha), cause)
        {
            RollbackFailure = rollbackFailure;
        }

        public Exception RollbackFailure { get; }

        private static string BuildMessage(Exception cause, Exception rollbackFailure, string? headBeforeSha)
        {
            // 이 클론은 버리기·Push가 금지라 도구 안에서 정리할 수 없다. 손으로 할 명령을 함께 준다.
            var command = headBeforeSha == null
                ? "git merge --abort"
                : $"git reset --hard {headBeforeSha}";
            return cause.Message + Environment.NewLine + Environment.NewLine +
                   "병합 전으로 되돌리지 못해 이 클론에 병합 도중의 상태가 남았을 수 있습니다. " +
                   $"Git 클라이언트에서 '{command}'로 정리한 뒤 다시 시도하세요." +
                   Environment.NewLine + "되돌리기 오류: " + rollbackFailure.Message;
        }
    }
}
