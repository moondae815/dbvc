using System;
using System.Diagnostics;
using System.IO;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// SSMS 셸 안에서만 실행되는 경로가 어디서 멈췄는지 파일에 남긴다.
    ///
    /// <see cref="Debug.WriteLine"/>은 <c>[Conditional("DEBUG")]</c>라 실제로 배포되는 Release
    /// VSIX에서는 호출 자체가 사라진다. 하필 <see cref="ObjectExplorerConnectionSource"/>는
    /// 개발 기계의 단위 테스트로 재현할 수 없고 사용자의 SSMS 프로세스 안에서만 깨지는 종류의
    /// 코드다 — 흔적이 없으면 원인을 추정하는 것 말고 할 수 있는 일이 없다.
    ///
    /// 도구 창이 보일 때마다 호출되므로 같은 사유가 연달아 반복되면 한 번만 적는다.
    /// 로그가 커지지 않게 하려는 것이 아니라, 파일을 열었을 때 "무슨 일이 있었는지"가
    /// 같은 줄 수백 개에 묻히지 않게 하려는 것이다.
    /// </summary>
    public static class SsmsDiagnostics
    {
        private static readonly object Lock = new object();
        private static string? _lastMessage;

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBVC",
            "ssms-diagnostics.log");

        public static void Trace(string message)
        {
            try
            {
                lock (Lock)
                {
                    if (string.Equals(_lastMessage, message, StringComparison.Ordinal))
                    {
                        return;
                    }
                    _lastMessage = message;

                    var directory = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.AppendAllText(
                        FilePath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                // 진단을 남기지 못하는 것이 기능을 막아서는 안 된다.
                Debug.WriteLine($"SsmsDiagnostics.Trace failed: {ex.Message}");
            }
        }
    }
}
