namespace DBVC.Baseline
{
    /// <summary>파싱 결과. 예외 대신 값으로 돌려주므로 판정이 순수 함수로 테스트된다.</summary>
    public sealed class BaselineOptionsResult
    {
        private BaselineOptionsResult(BaselineOptions? options, string? error)
        {
            Options = options;
            Error = error;
        }

        public BaselineOptions? Options { get; }
        public string? Error { get; }
        public bool IsValid => Options != null;

        internal static BaselineOptionsResult Ok(BaselineOptions options) => new(options, null);
        internal static BaselineOptionsResult Fail(string error) => new(null, error);
    }

    /// <summary>
    /// 명령줄 인자.
    ///
    /// <b>암호 속성이 없다.</b> 있으면 언젠가 렌더링되거나 로그에 실린다 — 암호는 파싱 결과에
    /// 담기지 않고 <see cref="Program"/>에서 자격증명 저장소로 곧장 들어간다.
    /// </summary>
    public sealed class BaselineOptions
    {
        private BaselineOptions(string server, string database, string repositoryPath, string? sqlUser)
        {
            Server = server;
            Database = database;
            RepositoryPath = repositoryPath;
            SqlUser = sqlUser;
        }

        public string Server { get; }
        public string Database { get; }
        public string RepositoryPath { get; }

        /// <summary><c>null</c>이면 Windows 통합 인증이다.</summary>
        public string? SqlUser { get; }

        public static string Usage =>
            "사용법: dbvc-baseline --server <서버> --database <DB> --repo <클론 경로> [--sql-user <계정>]";

        public static BaselineOptionsResult Parse(IReadOnlyList<string> args)
        {
            string? server = null, database = null, repo = null, sqlUser = null;

            for (int i = 0; i < args.Count; i++)
            {
                var flag = args[i];

                // 암호를 인자로 받지 않는다. 셸 이력과 프로세스 목록에 남기 때문이다.
                // 편의를 위해 열어 두면 반드시 쓰이므로 조용히 무시하지 않고 거부한다.
                if (flag == "--password" || flag == "-p")
                {
                    return BaselineOptionsResult.Fail(
                        "암호는 인자로 받지 않습니다. 셸 이력과 프로세스 목록에 남기 때문입니다. " +
                        "--sql-user만 주면 실행 중에 묻습니다.");
                }

                if (i + 1 >= args.Count)
                {
                    return BaselineOptionsResult.Fail($"'{flag}'에 값이 없습니다.\n{Usage}");
                }

                var value = args[++i];
                switch (flag)
                {
                    case "--server": server = value; break;
                    case "--database": database = value; break;
                    case "--repo": repo = value; break;
                    case "--sql-user": sqlUser = value; break;
                    default:
                        return BaselineOptionsResult.Fail($"모르는 옵션입니다: '{flag}'\n{Usage}");
                }
            }

            if (string.IsNullOrWhiteSpace(server))
            {
                return BaselineOptionsResult.Fail($"--server가 필요합니다.\n{Usage}");
            }
            if (string.IsNullOrWhiteSpace(database))
            {
                return BaselineOptionsResult.Fail($"--database가 필요합니다.\n{Usage}");
            }
            if (string.IsNullOrWhiteSpace(repo))
            {
                return BaselineOptionsResult.Fail($"--repo가 필요합니다.\n{Usage}");
            }

            return BaselineOptionsResult.Ok(new BaselineOptions(server!, database!, repo!, sqlUser));
        }
    }
}
