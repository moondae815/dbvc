using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Baseline
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            var parsed = BaselineOptions.Parse(args);
            if (!parsed.IsValid)
            {
                Console.Error.WriteLine(parsed.Error);
                return 3;
            }

            var options = parsed.Options!;

            var preflightError = PreflightCheck.Validate(Observe(options.RepositoryPath));
            if (preflightError != null)
            {
                var preflightReport = new BaselineReport
                {
                    Verdict = BaselineVerdict.PreflightFailed,
                    StopReason = preflightError
                };
                Console.Error.WriteLine(preflightReport.Render());
                return preflightReport.ExitCode;
            }

            var credentialStore = new SessionCredentialStore();
            if (options.SqlUser != null)
            {
                var password = ReadPasswordMasked($"{options.SqlUser}의 암호: ");
                credentialStore.Set(options.Server, options.Database, SqlAuthMode.Sql, options.SqlUser, password);
            }

            var tempDirectory = TempConfig.CreateDirectory();
            try
            {
                var configManager = new ConfigManager(TempConfig.MappingFilePath(tempDirectory));
                var smoManager = new SmoManager(configManager, credentialStore);

                var progress = new Progress<ExtractionProgress>(p =>
                    Console.Write($"\r{p.Completed}/{p.Total}  {p.CurrentObject}".PadRight(78)));

                var report = new BaselineRunner(configManager, smoManager).Run(options, progress);

                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine(report.Render());
                return report.ExitCode;
            }
            catch (Exception ex)
            {
                // 예기치 못한 예외까지 종료 코드 계약(런북이 읽는 0~3) 밖으로 나가면 안 된다.
                // 3은 "멈췄고 결과를 믿을 수 없다"는 뜻이므로 그대로 재사용한다.
                Console.Error.WriteLine($"예기치 못한 오류로 멈췄습니다: {ex.Message}");
                return 3;
            }
            finally
            {
                // 임시 매핑이 남으면 격리가 무너진다. 실패해도 반드시 지운다.
                try { Directory.Delete(tempDirectory, recursive: true); }
                catch (Exception) { /* 지우지 못해도 %TEMP%다. 실행 자체를 실패시키지 않는다. */ }
            }
        }

        private static PreflightInput Observe(string repositoryPath)
        {
            bool exists = Directory.Exists(repositoryPath);
            return new PreflightInput
            {
                DirectoryExists = exists,
                HasGitDirectory = exists && Directory.Exists(Path.Combine(repositoryPath, ".git")),
                HasSqlFiles = exists && Directory.EnumerateFiles(repositoryPath, "*.sql", SearchOption.AllDirectories).Any()
            };
        }

        /// <summary>
        /// 화면에 남기지 않고 암호를 읽는다. 인자로 받지 않는 이유와 같다 — 어깨너머와
        /// 터미널 스크롤백에 남기지 않는다.
        /// </summary>
        private static string ReadPasswordMasked(string prompt)
        {
            Console.Write(prompt);
            var buffer = new System.Text.StringBuilder();

            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter) break;

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0) buffer.Length--;
                    continue;
                }

                if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
            }

            Console.WriteLine();
            return buffer.ToString();
        }
    }
}
