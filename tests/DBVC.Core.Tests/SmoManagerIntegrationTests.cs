using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 실제 SQL Server에 대고 SMO 추출 경로를 실행한다.
    ///
    /// 이 경로는 오랫동안 어떤 테스트로도 실행되지 않았다. 테스트 프로젝트가 SMO 181(asm
    /// 18.100)을, DBVC.Core가 171(asm 17.100)을 참조해 런타임에 TypeLoadException이 났고,
    /// SmoManager가 그 예외를 삼켜 null을 돌려주었으며, 유일한 테스트가 "성공 또는 실패
    /// 둘 다 허용"으로 쓰여 있어 조용히 통과했다. 이 픽스처가 그 구멍을 막는다.
    ///
    /// 서버에 접속할 수 없으면 건너뛴다 — CI는 windows-latest이고 주 개발 환경은 macOS라
    /// 어느 쪽도 SQL Server를 보장하지 않는다. 실패로 만들면 없는 환경을 강요하게 된다.
    /// </summary>
    [TestFixture]
    public class SmoManagerIntegrationTests
    {
        private const string ServerName = "localhost";

        /// <summary>접속할 수 없으면 null. 그러면 모든 테스트가 건너뛴다.</summary>
        private static string? _database;
        private static string? _skipReason;
        private static SqlServerTestDatabase? _testDatabase;

        [OneTimeSetUp]
        public void CreateTestDatabase()
        {
            _testDatabase = SqlServerTestDatabase.TryCreate(out _skipReason);
            if (_testDatabase == null) return;

            // EnumerateTargets의 여러 갈래를 한 번에 지나가도록 타입을 섞는다.
            _testDatabase.ExecuteInOneSession(
                "CREATE TABLE dbo.Users (Id int IDENTITY(1,1) PRIMARY KEY, Name nvarchar(100) NOT NULL)",
                "CREATE VIEW dbo.vUsers AS SELECT Id, Name FROM dbo.Users",
                "CREATE PROCEDURE dbo.usp_GetUser @Id int AS SELECT Id, Name FROM dbo.Users WHERE Id = @Id",
                "CREATE FUNCTION dbo.fn_Double(@n int) RETURNS int AS BEGIN RETURN @n * 2 END",
                "CREATE TRIGGER dbo.trg_Users_Ins ON dbo.Users AFTER INSERT AS BEGIN SET NOCOUNT ON END",
                // 컬럼 정의만으로는 드러나지 않는 것들이다. 스크립팅 옵션이 꺼지면 조용히 사라진다.
                "ALTER TABLE dbo.Users ADD CreatedAt datetime2(7) NOT NULL " +
                "CONSTRAINT DF_Users_CreatedAt DEFAULT sysutcdatetime()",
                "CREATE NONCLUSTERED INDEX IX_Users_Name ON dbo.Users (Name)",
                "EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'사용자', " +
                "@level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'Users'");

            _database = _testDatabase.Name;
        }

        [OneTimeTearDown]
        public void DropTestDatabase() => _testDatabase?.Dispose();

        [SetUp]
        public void SkipWhenNoServer()
        {
            if (_database == null) Assert.Ignore(_skipReason ?? "SQL Server에 접속할 수 없습니다.");
        }

        [Test]
        public void ScriptObjectsDetailed_ExtractsEveryObjectType_FromARealDatabase()
        {
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null);

            Assert.That(result, Is.Not.Null, "추출이 시작조차 못 했습니다. SmoManager가 예외를 삼켰습니다.");
            Assert.That(result!.FailedObjects, Is.Empty);
            Assert.That(repo.RelativePaths(), Is.EquivalentTo(new[]
            {
                "dbo/Tables/Users.sql",
                "dbo/Views/vUsers.sql",
                "dbo/StoredProcedures/usp_GetUser.sql",
                "dbo/Functions/fn_Double.sql",
                "dbo/Triggers/trg_Users_Ins.sql"
            }));
        }

        [Test]
        public void ScriptObjectsDetailed_WritesTheActualCreateStatement()
        {
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null);
            Assert.That(result, Is.Not.Null, "Initial script failed");

            var sql = File.ReadAllText(Path.Combine(repo.Path, "dbo", "StoredProcedures", "usp_GetUser.sql"));
            Assert.That(sql, Does.Contain("CREATE").And.Contain("usp_GetUser"));
        }

        [Test]
        public void ScriptObjectsDetailed_IncludesConstraintsIndexesAndExtendedProperties_InTheTableScript()
        {
            // SMO의 ScriptingOptions 기본값은 이 셋이 모두 false다. 켜지 않으면 테이블 .sql에
            // 컬럼 정의만 남고, 그 파일로 만든 배포 스크립트는 테이블을 재생산하지 못한다.
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, new List<string> { "dbo.Users" });
            Assert.That(result, Is.Not.Null, "Initial script failed");

            var sql = File.ReadAllText(Path.Combine(repo.Path, "dbo", "Tables", "Users.sql"));

            Assert.Multiple(() =>
            {
                Assert.That(sql, Does.Contain("PRIMARY KEY"), "기본 키가 빠졌습니다");
                Assert.That(sql, Does.Contain("DF_Users_CreatedAt"), "기본값 제약이 빠졌습니다");
                Assert.That(sql, Does.Contain("IX_Users_Name"), "인덱스가 빠졌습니다");
                Assert.That(sql, Does.Contain("MS_Description"), "확장 속성이 빠졌습니다");
            });
        }

        [Test]
        public void ScriptObjectsDetailed_ExtractsOnlyTheNamedObjects_WhenFiltered()
        {
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, new List<string> { "dbo.Users" });

            Assert.That(result, Is.Not.Null, "Initial script failed");
            Assert.That(result.SucceededCount, Is.EqualTo(1));
            Assert.That(repo.RelativePaths(), Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
        }

        [Test]
        public void ScriptObjectsDetailed_SucceedsWithoutCreatingFiles_WhenNothingMatchesTheFilter()
        {
            // 지운 SmoManagerTests의 테스트가 여기서 걸렸다. 추출할 것이 없어도 성공이고,
            // 성공했다고 폴더가 생기지는 않는다. 사용자 객체가 없는 데이터베이스가 같은 경우다.
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, new List<string> { "dbo.NoSuchObject" });

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SucceededCount, Is.EqualTo(0));
            Assert.That(result.FailedObjects, Is.Empty);
            Assert.That(repo.RelativePaths(), Is.Empty);
        }

        [Test]
        public void ScriptObjectsDetailed_DoesNotRewriteFilesWhoseContentDidNotChange()
        {
            // ScriptAll의 단위 테스트는 가짜 스크립터로 검증한다. 실제 SMO 출력이 두 번의 호출에서
            // 바이트까지 같은지는 여기서만 알 수 있고, 같지 않으면 git status 최적화가 무너진다.
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null);
            Assert.That(result, Is.Not.Null, "Initial script failed");

            var path = Path.Combine(repo.Path, "dbo", "Tables", "Users.sql");
            var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);

            var result2 = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null);
            Assert.That(result2, Is.Not.Null, "Second script failed");

            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp),
                "SMO가 같은 내용을 냈는데도 파일을 다시 썼습니다. git이 전 파일을 다시 해시하게 됩니다.");
        }

        [Test]
        public void ScriptObjectsDetailed_ReportsProgressForEveryObject()
        {
            using var repo = new TempRepo(_database!);
            var reported = new List<ExtractionProgress>();

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null,
                new ImmediateProgress(reported.Add), CancellationToken.None);

            Assert.That(result, Is.Not.Null, "Initial script failed");
            Assert.That(reported, Is.Not.Empty);
            Assert.That(reported[reported.Count - 1].Completed, Is.EqualTo(reported[reported.Count - 1].Total));
            Assert.That(reported.Select(p => p.Completed), Is.Ordered);
        }

        [Test]
        public void ScriptObjectsDetailed_StopsAndPropagatesCancellation()
        {
            using var repo = new TempRepo(_database!);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null, null, cts.Token));

            Assert.That(repo.RelativePaths(), Is.Empty, "취소된 추출이 파일을 남겼습니다.");
        }

        [Test]
        public void ScriptObjects_EmitsCreateOrAlter_ForProcedures()
        {
            // SMO 옵션이 실제로 어떤 텍스트를 뱉는지는 서버에 붙어야만 확인된다.
            // 설계 3.6의 배포 스크립트 3분류가 전부 이 텍스트에 걸려 있다.
            //
            // 이 fixture의 _database는 OneTimeSetUp에서 한 번 만들어 모든 테스트가 공유한다.
            // 여기서 만든 프로브 객체가 남으면 ScriptObjectsDetailed_ExtractsEveryObjectType_...처럼
            // "정확히 이 5개"를 단정하는 테스트를 깨뜨리므로 finally에서 반드시 지운다.
            _testDatabase!.Execute("CREATE PROCEDURE dbo.CreateOrAlterProbe AS SELECT 1");
            try
            {
                using var repo = new TempRepo(_database!);
                repo.Smo.ScriptObjects(ServerName, _database!, new List<string> { "dbo.CreateOrAlterProbe" });

                var relative = repo.RelativePaths().Single(p => p.EndsWith("CreateOrAlterProbe.sql", StringComparison.OrdinalIgnoreCase));
                var sql = File.ReadAllText(Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar)));

                Assert.That(sql, Does.Contain("CREATE OR ALTER").IgnoreCase);
            }
            finally
            {
                _testDatabase.Execute("DROP PROCEDURE dbo.CreateOrAlterProbe");
            }
        }

        [Test]
        public void ScriptObjects_KeepsPlainCreate_ForTables()
        {
            // T-SQL에 CREATE OR ALTER TABLE이 없다. 기존 테이블 변경이 자동화 불가인 근거다(설계 2.4).
            _testDatabase!.Execute("CREATE TABLE dbo.CreateOrAlterTableProbe (Id int NOT NULL)");
            try
            {
                using var repo = new TempRepo(_database!);
                repo.Smo.ScriptObjects(ServerName, _database!, new List<string> { "dbo.CreateOrAlterTableProbe" });

                var relative = repo.RelativePaths().Single(p => p.EndsWith("CreateOrAlterTableProbe.sql", StringComparison.OrdinalIgnoreCase));
                var sql = File.ReadAllText(Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar)));

                Assert.That(sql, Does.Contain("CREATE TABLE").IgnoreCase);
                Assert.That(sql, Does.Not.Contain("CREATE OR ALTER").IgnoreCase);
            }
            finally
            {
                _testDatabase.Execute("DROP TABLE dbo.CreateOrAlterTableProbe");
            }
        }

        [Test]
        public void CompareWithRepository_ReportsInSync_RightAfterAFullExtraction()
        {
            // 이 설계 전체가 SMO 출력의 결정성에 기댄다. 흔들리면 전부 Modified로 나오고
            // 화면이 무의미해진다. 깨지면 대비책은 텍스트 정규화(BOM·개행·후행 공백) 비교로
            // 떨어뜨리는 것이다.
            _testDatabase!.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            _testDatabase.Execute("CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NULL)");
            try
            {
                var repoPath = NewTempDir();
                var config = NewConfig(_testDatabase, repoPath, MappingMode.Write);
                new SmoManager(config).ScriptObjectsDetailed(ServerName, _database!);

                // 비교는 mode가 write가 아니어야 돈다. 같은 저장소를 배포 용도로 다시 매핑한다.
                var deployConfig = NewConfig(_testDatabase, repoPath, MappingMode.Deploy);
                var result = new SmoManager(deployConfig).CompareWithRepository(ServerName, _database!);

                Assert.That(result, Is.Not.Null);
                Assert.That(result!.Differences.Select(d => d.QualifiedName), Is.Empty);
                Assert.That(result.ComparedCount, Is.GreaterThan(0));
            }
            finally
            {
                _testDatabase.Execute("DROP PROCEDURE dbo.GetOne");
                _testDatabase.Execute("DROP TABLE dbo.Widgets");
            }
        }

        [Test]
        public void CompareWithRepository_WritesNothingIntoTheRepository()
        {
            // 저장소를 건드리지 않는다는 것이 이 방식을 고른 이유다. 한 글자라도 쓰면
            // 되돌리는 단계가 필요해지고, 그 단계가 실패하는 날 작업 트리가 망가진다.
            _testDatabase!.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            try
            {
                var repoPath = NewTempDir();
                var deployConfig = NewConfig(_testDatabase, repoPath, MappingMode.Deploy);

                new SmoManager(deployConfig).CompareWithRepository(ServerName, _database!);

                Assert.That(Directory.GetFileSystemEntries(repoPath), Is.Empty);
            }
            finally
            {
                _testDatabase.Execute("DROP PROCEDURE dbo.GetOne");
            }
        }

        [Test]
        public void CompareWithRepository_ReportsOnlyTheAlteredObject_AsModified()
        {
            _testDatabase!.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            _testDatabase.Execute("CREATE PROCEDURE dbo.GetTwo AS SELECT 2");
            try
            {
                var repoPath = NewTempDir();
                new SmoManager(NewConfig(_testDatabase, repoPath, MappingMode.Write))
                    .ScriptObjectsDetailed(ServerName, _database!);

                _testDatabase.Execute("ALTER PROCEDURE dbo.GetOne AS SELECT 99");

                var result = new SmoManager(NewConfig(_testDatabase, repoPath, MappingMode.Deploy))
                    .CompareWithRepository(ServerName, _database!);

                Assert.That(result!.Differences.Count, Is.EqualTo(1));
                Assert.That(result.Differences[0].QualifiedName, Is.EqualTo("dbo.GetOne"));
                Assert.That(result.Differences[0].State, Is.EqualTo(ObjectDiffState.Modified));
            }
            finally
            {
                _testDatabase.Execute("DROP PROCEDURE dbo.GetOne");
                _testDatabase.Execute("DROP PROCEDURE dbo.GetTwo");
            }
        }

        [Test]
        public void CompareWithRepository_ReportsMissingInBranch_WhenTheFileWasDeleted()
        {
            _testDatabase!.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            try
            {
                var repoPath = NewTempDir();
                new SmoManager(NewConfig(_testDatabase, repoPath, MappingMode.Write))
                    .ScriptObjectsDetailed(ServerName, _database!);
                File.Delete(Path.Combine(repoPath, "dbo", "StoredProcedures", "GetOne.sql"));

                var result = new SmoManager(NewConfig(_testDatabase, repoPath, MappingMode.Deploy))
                    .CompareWithRepository(ServerName, _database!);

                var one = result!.Differences.Single(d => d.QualifiedName == "dbo.GetOne");
                Assert.That(one.State, Is.EqualTo(ObjectDiffState.MissingInBranch));
            }
            finally
            {
                _testDatabase.Execute("DROP PROCEDURE dbo.GetOne");
            }
        }

        [Test]
        public void CompareWithRepository_ReportsMissingInDatabase_WhenTheObjectWasDropped()
        {
            _testDatabase!.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            try
            {
                var repoPath = NewTempDir();
                new SmoManager(NewConfig(_testDatabase, repoPath, MappingMode.Write))
                    .ScriptObjectsDetailed(ServerName, _database!);
                _testDatabase.Execute("DROP PROCEDURE dbo.GetOne");

                var result = new SmoManager(NewConfig(_testDatabase, repoPath, MappingMode.Deploy))
                    .CompareWithRepository(ServerName, _database!);

                var one = result!.Differences.Single(d => d.QualifiedName == "dbo.GetOne");
                Assert.That(one.State, Is.EqualTo(ObjectDiffState.MissingInDatabase));
            }
            finally
            {
                // 정상 경로에서 이미 DROP했다 — 여기 오기 전 어디서든 예외가 나면 그 DROP이
                // 아직 안 됐을 수 있으므로 존재할 때만 지운다. 무조건 DROP하면 정상 경로를
                // 지난 뒤엔 이미 없는 객체를 지우려다 finally 자체가 던져, 원래 예외를 가린다.
                _testDatabase.Execute("IF OBJECT_ID(N'dbo.GetOne', N'P') IS NOT NULL DROP PROCEDURE dbo.GetOne");
            }
        }

        [Test]
        public void GeneratedDeploymentScript_RunsAgainstADatabaseThatAlreadyHasTheObjects()
        {
            // 저장소 파일이 CREATE OR ALTER로 저장되어 있다는 1차의 결정이 실제로
            // 실행 가능한 스크립트를 만드는지 확인하는 유일한 자리다.
            _testDatabase!.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            _testDatabase.Execute("CREATE VIEW dbo.OneView AS SELECT 1 AS N");
            try
            {
                var repoPath = NewTempDir();
                var config = NewConfig(_testDatabase, repoPath, MappingMode.Write);
                new SmoManager(config).ScriptObjectsDetailed(ServerName, _database!);

                _testDatabase.Execute("ALTER PROCEDURE dbo.GetOne AS SELECT 42");

                var deployConfig = NewConfig(_testDatabase, repoPath, MappingMode.Deploy);
                var result = new SmoManager(deployConfig).CompareWithRepository(ServerName, _database!);
                var export = new ScriptExporter(deployConfig, new GitManager(deployConfig))
                    .ExportFromComparison(ServerName, _database!, result!.Differences, DateTimeOffset.Now);

                Assert.That(export.HasContent, Is.True);

                // 객체가 이미 있는 DB에 그대로 실행한다. "이미 있습니다"가 나오면 실패다.
                Assert.DoesNotThrow(() => _testDatabase.ExecuteScript(export.Script));

                // 실행 뒤에는 저장소와 일치해야 한다. 3단계 루프가 실제로 닫히는지 본다.
                var after = new SmoManager(deployConfig).CompareWithRepository(ServerName, _database!);
                Assert.That(after!.Differences.Select(d => d.QualifiedName), Does.Not.Contain("dbo.GetOne"));
            }
            finally
            {
                _testDatabase.Execute("DROP PROCEDURE dbo.GetOne");
                _testDatabase.Execute("DROP VIEW dbo.OneView");
            }
        }

        [Test]
        public void ScriptObjectToText_ReturnsTheCurrentDefinition_WithoutTouchingTheRepository()
        {
            _testDatabase!.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            try
            {
                var repoPath = NewTempDir();
                var config = NewConfig(_testDatabase, repoPath, MappingMode.Deploy);

                var text = new SmoManager(config).ScriptObjectToText(ServerName, _database!, "dbo.GetOne");

                Assert.That(text, Does.Contain("GetOne"));
                Assert.That(Directory.GetFileSystemEntries(repoPath), Is.Empty);
            }
            finally
            {
                _testDatabase.Execute("DROP PROCEDURE dbo.GetOne");
            }
        }

        [Test]
        public void CompareWithRepository_Throws_WhenCancelledMidway()
        {
            _testDatabase!.Execute("CREATE PROCEDURE dbo.GetOne AS SELECT 1");
            _testDatabase.Execute("CREATE PROCEDURE dbo.GetTwo AS SELECT 2");
            try
            {
                var repoPath = NewTempDir();
                var config = NewConfig(_testDatabase, repoPath, MappingMode.Deploy);

                using var cts = new CancellationTokenSource();
                var progress = new SimpleProgress<ExtractionProgress>(_ => cts.Cancel());

                Assert.Throws<OperationCanceledException>(
                    () => new SmoManager(config).CompareWithRepository(ServerName, _database!, progress, cts.Token));
            }
            finally
            {
                _testDatabase.Execute("DROP PROCEDURE dbo.GetOne");
                _testDatabase.Execute("DROP PROCEDURE dbo.GetTwo");
            }
        }

        /// <summary>
        /// 임시 저장소 폴더 하나. 매핑 파일은 여기 담지 않는다 — 안에 넣으면 "저장소를
        /// 건드리지 않는다"를 확인하는 테스트가 mappings.json을 발견해 거짓으로 실패한다.
        /// </summary>
        private static string NewTempDir()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbvc_it_repo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// 매 호출마다 새 임시 mappings.json을 만든다. 같은 파일을 공유하면 write 매핑을
        /// deploy로 다시 등록할 때 앞의 등록을 조용히 덮어써 버려, 두 매핑을 나눠 쓰는
        /// 테스트의 의도가 사라진다. Branch는 비운다 — 저장소가 아직 커밋되지 않았을 수 있어
        /// 고정하지 않는다.
        /// </summary>
        private static ConfigManager NewConfig(SqlServerTestDatabase db, string repoPath, MappingMode mode)
        {
            var configPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "dbvc_it_cfg_" + Guid.NewGuid().ToString("N"), "mappings.json");
            var config = new ConfigManager(configPath);
            config.AddMapping(new MappingConfig
            {
                ServerName = ServerName,
                DatabaseName = db.Name,
                GitPath = repoPath,
                Mode = mode
            });
            return config;
        }

        /// <summary>매핑까지 갖춘 임시 저장소 폴더. 사용자의 실제 mappings.json을 건드리지 않는다.</summary>
        private sealed class TempRepo : IDisposable
        {
            public string Path { get; }
            public SmoManager Smo { get; }

            public TempRepo(string database)
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbvc_it_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);

                var config = new ConfigManager(System.IO.Path.Combine(Path, "mappings.json"));
                config.AddMapping(ServerName, database, Path);
                Smo = new SmoManager(config);
            }

            public string[] RelativePaths()
                => Directory.GetFiles(Path, "*.sql", SearchOption.AllDirectories)
                    .Select(f => f.Substring(Path.Length + 1).Replace('\\', '/'))
                    .OrderBy(p => p)
                    .ToArray();

            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch { }
            }
        }

        private sealed class ImmediateProgress : IProgress<ExtractionProgress>
        {
            private readonly Action<ExtractionProgress> _onReport;
            public ImmediateProgress(Action<ExtractionProgress> onReport) { _onReport = onReport; }
            public void Report(ExtractionProgress value) => _onReport(value);
        }

        /// <summary>보고 콜백을 받는 범용 <see cref="IProgress{T}"/>. 첫 보고에서 취소하는 것처럼
        /// 진행 상황 자체를 신경 쓰지 않는 테스트를 위한 것이다.</summary>
        private sealed class SimpleProgress<T> : IProgress<T>
        {
            private readonly Action<T> _onReport;
            public SimpleProgress(Action<T> onReport) { _onReport = onReport; }
            public void Report(T value) => _onReport(value);
        }
    }
}
