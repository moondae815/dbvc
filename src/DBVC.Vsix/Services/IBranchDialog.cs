using System.Collections.Generic;
using DBVC.Core.Models;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 브랜치 이름을 받고 고르게 하는 창. 인터페이스로 두는 이유는 나머지 대화상자와 같다 -
    /// WPF 창을 띄우지 않고 ViewModel을 테스트하기 위해서다.
    /// </summary>
    public interface IBranchDialog
    {
        /// <summary>새 브랜치 이름을 받는다. 취소하면 null이다.</summary>
        string? AskNewName();

        /// <summary>갈아탈 브랜치를 고르게 한다. 취소하면 null이다.</summary>
        string? AskExisting(IReadOnlyList<BranchInfo> branches);
    }
}
