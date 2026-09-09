namespace DBVC.Baseline
{
    /// <summary>파일시스템을 보지 않는다. 관측은 호출자가 하고 판정만 여기서 한다.</summary>
    public sealed class PreflightInput
    {
        public bool DirectoryExists { get; set; }
        public bool HasGitDirectory { get; set; }
        public bool HasSqlFiles { get; set; }
    }

    /// <summary>
    /// 기준선을 받아도 되는 폴더인지 판정한다.
    ///
    /// 막으려는 사고는 하나다 — 실수로 개발 클론을 가리켜 운영 사진으로 덮어쓰는 것.
    /// 작업 트리가 깨끗한지는 보지 않는다: GitManager 의존이 생기는 데 비해 얻는 것이 없고,
    /// master에 갓 체크아웃한 클론은 정의상 .sql이 없다.
    /// </summary>
    public static class PreflightCheck
    {
        public static string? Validate(PreflightInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // 순서가 곧 우선순위다. 폴더가 없으면 나머지 관측값은 뜻이 없다.
            if (!input.DirectoryExists)
            {
                return "--repo로 지정한 폴더가 없습니다.";
            }

            if (!input.HasGitDirectory)
            {
                return "--repo로 지정한 폴더가 git 저장소가 아닙니다. master를 체크아웃한 클론을 지정하세요.";
            }

            if (input.HasSqlFiles)
            {
                return "--repo로 지정한 폴더에 이미 .sql 파일이 있습니다. " +
                       "개발 클론을 지정하지 않았는지 확인하세요 — 기준선은 비어 있는 master 클론에만 만듭니다.";
            }

            return null;
        }
    }
}
