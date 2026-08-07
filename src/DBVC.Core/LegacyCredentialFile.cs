using System;
using System.Diagnostics;
using System.IO;

namespace DBVC.Core
{
    /// <summary>
    /// DBVC가 예전에 남긴 <c>%APPDATA%\DBVC\credentials.json</c>을 지운다.
    ///
    /// 그 파일에는 DPAPI로 보호한 SQL 인증 암호가 들어 있었다. 이제 자격증명은 프로세스
    /// 메모리에만 두므로 아무도 읽지 않는 파일이 되는데, 읽히지 않는다고 남겨 두면
    /// "디스크에 자격증명을 남기지 않는다"는 결정과 어긋난 채 방치된다.
    ///
    /// 한 번 지우면 다시 생기지 않는다. 멱등이므로 여러 번 불려도 무해하다.
    /// </summary>
    public static class LegacyCredentialFile
    {
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBVC",
            "credentials.json");

        /// <summary>
        /// 파일이 있으면 지운다. <b>예외를 던지지 않는다</b> — 이 정리에 실패하는 것과
        /// 확장이 뜨지 않는 것은 비교할 문제가 아니다.
        ///
        /// 디렉터리는 건드리지 않는다. 같은 폴더에 <c>mappings.json</c>이 있다.
        /// </summary>
        /// <param name="path">테스트용 경로 재정의. <c>null</c>이면 <see cref="DefaultPath"/>.</param>
        public static void DeleteIfPresent(string? path = null)
        {
            var target = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;

            try
            {
                if (!File.Exists(target))
                {
                    return;
                }

                File.Delete(target);
                Debug.WriteLine($"LegacyCredentialFile: '{target}'을(를) 지웠습니다.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LegacyCredentialFile: '{target}'을(를) 지우지 못했습니다: {ex.Message}");
            }
        }
    }
}
