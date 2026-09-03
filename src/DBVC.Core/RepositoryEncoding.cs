using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DBVC.Core
{
    /// <summary>저장소에 쌓인 추출물의 인코딩 세대.</summary>
    public enum RepositoryEncodingKind
    {
        /// <summary>판정할 근거가 없다. 갓 연결한 저장소이거나 읽을 수 없다.</summary>
        Unknown,

        /// <summary>0.5.15 이전이 쓴 UTF-16LE. Git이 바이너리로 취급해 diff도 병합도 되지 않는다.</summary>
        Legacy,

        /// <summary>UTF-8. Git이 텍스트로 본다.</summary>
        Current
    }

    /// <summary>
    /// 저장소의 추출물이 옛 인코딩(UTF-16LE)인지 판정하고, 줄바꿈 변환을 끄는 .gitattributes를 만든다.
    ///
    /// <see cref="ExtractionBaseline"/>과 합치지 않는다. 그쪽은 "추출물이 있는가"라는 다른 질문에
    /// 답하고 이미 새로고침의 분기를 정하는 데 쓰이고 있어, 인코딩 판정을 얹으면 한 함수가
    /// 두 결정을 하게 된다.
    /// </summary>
    public static class RepositoryEncoding
    {
        /// <summary>
        /// SMO가 쓰는 줄바꿈은 CRLF다. 변환을 끄면 작업 트리와 블롭의 바이트가 같아진다 —
        /// Diff의 Old는 블롭에서(GetFileContentAtHead), New는 작업 트리에서(ReadWorkingTreeFile)
        /// 오므로, 변환이 끼면 DiffPlex가 모든 줄을 변경으로 판정한다. 양쪽을 정규화하는 코드는 없다.
        /// 텍스트 diff와 3-way 병합은 -text와 무관하게 그대로 동작한다(실측 확인).
        /// </summary>
        public const string GitAttributesContent =
            "# DBVC가 추출하는 .sql은 SMO가 CRLF로 쓴다. 줄바꿈 변환을 끄면 작업 트리와 블롭의\r\n" +
            "# 바이트가 같아진다 — Diff의 Old는 블롭에서, New는 작업 트리에서 오므로 변환이 끼면\r\n" +
            "# 모든 줄이 변경으로 보인다. 텍스트 diff와 3-way 병합은 -text와 무관하게 동작한다.\r\n" +
            "*.sql -text\r\n";

        /// <summary>UTF-16LE BOM. 이 두 바이트로 시작하면 0.5.15 이전이 쓴 파일이다.</summary>
        private const byte Utf16LeBom0 = 0xFF;
        private const byte Utf16LeBom1 = 0xFE;

        /// <summary>
        /// 규약을 따르는 <c>.sql</c>을 <b>처음 하나만</b> 찾아 앞 2바이트를 본다.
        ///
        /// 전부를 훑지 않는 이유는 <see cref="ExtractionBaseline.Exists"/>와 같다. 저장소가 한
        /// 인코딩으로 통일되어 있다는 전제가 깨지는 경우는 전환이 중간에 멈춘 때뿐이고,
        /// 그때는 다시 눌러 이어가면 된다(전체 추출은 멱등이다).
        ///
        /// 판정하지 못하면 <see cref="RepositoryEncodingKind.Unknown"/>이다. 배너를 띄우지 않는
        /// 쪽이, 멀쩡한 저장소에 전 파일 재작성을 권하는 쪽보다 안전하다.
        /// </summary>
        public static RepositoryEncodingKind Detect(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath)) return RepositoryEncodingKind.Unknown;

            try
            {
                if (!Directory.Exists(repoPath)) return RepositoryEncodingKind.Unknown;

                // 전체를 훑지 않는다. 규약이 정확히 2단계이므로 그 모양만 따라간다.
                foreach (var schemaDir in Directory.EnumerateDirectories(repoPath))
                {
                    var schema = Path.GetFileName(schemaDir);

                    foreach (var typeDir in Directory.EnumerateDirectories(schemaDir))
                    {
                        var folder = Path.GetFileName(typeDir);

                        foreach (var file in Directory.EnumerateFiles(typeDir, "*.sql"))
                        {
                            var relativePath = $"{schema}/{folder}/{Path.GetFileName(file)}";

                            if (!ObjectPathConvention.TryParseRelativePath(relativePath, out _, out var objectType, out _)
                                || objectType == ObjectPathConvention.UnknownFolder)
                            {
                                continue;
                            }

                            return ReadKind(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RepositoryEncoding.Detect failed for '{repoPath}': {ex.Message}");
            }

            return RepositoryEncodingKind.Unknown;
        }

        private static RepositoryEncodingKind ReadKind(string path)
        {
            // FileShare.ReadWrite로 연다. 추출이 방금 쓴 파일을 백신이 잡고 있을 수 있고,
            // 그때 판정에 실패해 예외가 나면 배너 하나 때문에 접속 전체가 실패한다.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[2];

                // 2바이트도 안 되는 파일은 UTF-16 BOM일 수 없다.
                if (stream.Read(head, 0, 2) < 2) return RepositoryEncodingKind.Current;

                return head[0] == Utf16LeBom0 && head[1] == Utf16LeBom1
                    ? RepositoryEncodingKind.Legacy
                    : RepositoryEncodingKind.Current;
            }
        }

        /// <summary>
        /// 줄바꿈 변환을 끄는 <c>.gitattributes</c>를 저장소 루트에 만든다.
        /// 이미 있으면 건드리지 않는다 - 사용자가 손으로 넣은 규칙을 덮어쓰지 않기 위해서다.
        /// </summary>
        /// <returns>새로 만들었으면 true. 이미 있었거나 쓰지 못했으면 false.</returns>
        public static bool EnsureGitAttributes(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath)) return false;

            try
            {
                var path = Path.Combine(repoPath, ".gitattributes");
                if (File.Exists(path)) return false;

                // BOM 없이 쓴다. 저장소 .sql에 BOM을 붙이는 것과 목적이 다르다 - 이쪽은 Git이
                // 읽는 설정 파일이고, BOM이 붙으면 첫 줄을 규칙으로 알아보지 못할 위험이 있다.
                File.WriteAllText(path, GitAttributesContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RepositoryEncoding.EnsureGitAttributes failed for '{repoPath}': {ex.Message}");
                return false;
            }
        }
    }
}
