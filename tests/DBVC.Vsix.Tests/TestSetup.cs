using NUnit.Framework;
using DBVC.Vsix.Services;

// 네임스페이스 밖에 두어 이 어셈블리의 모든 테스트에 적용되게 한다.
[SetUpFixture]
public class TestSetup
{
    /// <summary>
    /// 테스트는 자동 채움의 실패 경로를 일부러 실행한다. 진단을 켜 둔 채로 두면
    /// 개발 기계의 실제 %APPDATA%\DBVC\ssms-diagnostics.log가 테스트 잡음으로 채워져,
    /// SSMS에서 실제로 무슨 일이 있었는지 읽을 수 없게 된다.
    /// </summary>
    [OneTimeSetUp]
    public void DisableSsmsDiagnostics()
    {
        SsmsDiagnostics.Enabled = false;
    }
}
