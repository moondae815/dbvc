using System.Text;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// SMO의 <c>ScriptingOptions.Encoding</c> 세터는 내부적으로 코드페이지 1252를 요구한다.
    /// .NET Framework에는 그 인코딩이 들어 있지만 .NET 10에는 기본 등록되어 있지 않아,
    /// net10.0 테스트 호스트에서만 <c>NotSupportedException: No data is available for
    /// encoding 1252</c>가 난다.
    ///
    /// 제품 문제가 아니다 — DBVC.Vsix는 SSMS 안에서 net48로 돌고, 같은 테스트가 net48에서는
    /// 그대로 통과한다. 여기서 등록하는 것은 <b>테스트 호스트를 실제 런타임에 맞추는 것</b>이며,
    /// Core가 스스로 등록하게 두지 않는 이유는 라이브러리가 프로세스 전역 상태를 바꾸면
    /// 그것을 올린 다른 코드까지 영향을 받기 때문이다.
    /// </summary>
    [SetUpFixture]
    public class TestHostEncodingSetup
    {
        [OneTimeSetUp]
        public void RegisterCodePages()
        {
#if !NETFRAMEWORK
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
        }
    }
}
