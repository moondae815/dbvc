using System;
using System.Diagnostics;
using System.IO;

namespace DBVC.Core
{
    /// <summary>
    /// 작업 트리에 DBVC가 추출해 둔 기준선이 있는지 판정한다.
    ///
    /// 변경분만 추출하는 새로고침은 "나머지 객체의 파일은 이미 저장소에 있다"를 전제한다.
    /// 그 전제가 깨진 상태(저장소를 갓 연결한 직후)에서 변경분만 추출하면 저장소가 거의 빈 채로
    /// 남고, 사용자는 커밋할 것을 찾지 못한다. 그 상황을 자동으로 알아채기 위한 검사다.
    /// </summary>
    public static class ExtractionBaseline
    {
        /// <summary>
        /// <c>[Schema]/[ObjectType]/[Name].sql</c> 규약을 따르는 파일이 하나라도 있으면 true.
        ///
        /// 판정을 일부러 엄격하게 잡았다. 잘못 true를 내면(기준선이 없는데 있다고 보면) 저장소가
        /// 빈 채로 남지만, 잘못 false를 내면 전체 추출이 한 번 더 도는 것으로 끝난다.
        /// 그래서 타입 폴더 이름이 DBVC가 아는 것일 때만 인정한다 — 사용자가 손으로 넣어 둔
        /// 3단계 .sql을 기준선으로 착각하지 않는다.
        /// </summary>
        public static bool Exists(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath)) return false;

            try
            {
                if (!Directory.Exists(repoPath)) return false;

                // 전체를 훑지 않는다. 규약이 정확히 2단계이므로 그 모양만 따라가고 처음 찾으면 멈춘다.
                foreach (var schemaDir in Directory.EnumerateDirectories(repoPath))
                {
                    var schema = Path.GetFileName(schemaDir);

                    foreach (var typeDir in Directory.EnumerateDirectories(schemaDir))
                    {
                        var folder = Path.GetFileName(typeDir);

                        foreach (var file in Directory.EnumerateFiles(typeDir, "*.sql"))
                        {
                            var relativePath = $"{schema}/{folder}/{Path.GetFileName(file)}";

                            if (ObjectPathConvention.TryParseRelativePath(relativePath, out _, out var objectType, out _)
                                && objectType != ObjectPathConvention.UnknownFolder)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 읽을 수 없으면 "기준선이 없다"로 본다. 전체 추출이 한 번 더 도는 쪽이 안전하다.
                Debug.WriteLine($"ExtractionBaseline.Exists failed for '{repoPath}': {ex.Message}");
            }

            return false;
        }
    }
}
