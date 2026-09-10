using System;
using System.Collections.Generic;

namespace DBVC.Core.Models
{
    /// <summary>
    /// 브랜치 조작의 결과. 실패를 예외로 던지지 않는 이유는 거부가 예외적 사건이 아니기
    /// 때문이다 - 더러운 트리에서 전환을 누르는 것은 정상적인 사용자 행동이고, 화면은
    /// 무엇이 막았는지를 목록으로 보여 줘야 한다.
    /// </summary>
    public class BranchResult
    {
        public bool Succeeded { get; set; }

        /// <summary>실패 사유. 성공이면 null이다. 한국어이며 화면이 그대로 띄운다.</summary>
        public string? Message { get; set; }

        /// <summary>전환을 막은 미커밋 파일들. 그 외의 경우에는 빈 목록이다.</summary>
        public IReadOnlyList<string> BlockingPaths { get; set; } = Array.Empty<string>();

        public static BranchResult Ok() => new BranchResult { Succeeded = true };

        public static BranchResult Fail(string message, IReadOnlyList<string>? blocking = null) =>
            new BranchResult
            {
                Succeeded = false,
                Message = message,
                BlockingPaths = blocking ?? Array.Empty<string>()
            };
    }
}
