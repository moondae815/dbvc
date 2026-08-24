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
    [TestFixture]
    public class SmoManagerTests
    {
        // ScriptObjects_GivenValidDb_...를 여기서 지웠다. 두 가지가 잘못되어 있었다.
        //
        // 첫째, 기본 경로의 ConfigManager를 써서 사용자의 실제 %APPDATA%\DBVC\mappings.json에
        // localhost/master 매핑을 남겼다 — 테스트가 개발자의 설정 파일을 오염시켰다.
        //
        // 둘째, "성공했으면 파일이 있어야 한다"를 단정했는데 master에는 사용자 객체가 없어
        // 성공해도 파일이 생기지 않는다. 그런데도 오랫동안 통과했다 — 성공/실패 어느 쪽이든
        // 넘어가도록 쓰여 있었고, 실제로는 SMO 버전 불일치로 늘 실패 갈래를 탔기 때문이다.
        //
        // 실제 데이터베이스에 대고 하는 검증은 SmoManagerIntegrationTests가 맡는다.

        [Test]
        public void ScriptObjects_WithInvalidServerOrDb_ReturnsFalse()
        {
            var smo = new SmoManager();
            bool result = smo.ScriptObjects("invalid_server_xyz", "invalid_db_xyz");
            Assert.That(result, Is.False);
        }

        [Test]
        [TestCase(null, "master")]
        [TestCase("", "master")]
        [TestCase("   ", "master")]
        [TestCase("localhost", null)]
        [TestCase("localhost", "")]
        [TestCase("localhost", "   ")]
        public void ScriptObjects_WithNullOrWhitespaceServerOrDb_ReturnsFalse(string? serverName, string? databaseName)
        {
            var smo = new SmoManager();
            bool result = smo.ScriptObjects(serverName!, databaseName!);
            Assert.That(result, Is.False);
        }

        [Test]
        public void SmoManager_Constructor_DefaultConfigManager_Instantiates()
        {
            var smo = new SmoManager();
            Assert.That(smo, Is.Not.Null);
        }

        [Test]
        public void BuildScriptingOptions_EnablesCreateOrAlter()
        {
            // 저장소 파일이 그대로 실행 가능해야 배포 스크립트를 파일에서 만들 수 있다.
            // 순수 CREATE로 두면 객체가 이미 있는 대상에서 첫 문장부터 실패한다.
            var options = SmoManager.BuildScriptingOptions();

            Assert.That(options.ScriptForCreateOrAlter, Is.True);
        }

        [Test]
        public void BuildScriptingOptions_StillDoesNotScriptDropsOrExistenceChecks()
        {
            // CREATE OR ALTER는 이 둘과 함께 켜면 SMO가 무엇을 뱉을지 예측하기 어렵다.
            // 기존 결정을 그대로 지킨다는 것을 여기서 못박는다.
            var options = SmoManager.BuildScriptingOptions();

            Assert.That(options.ScriptDrops, Is.False);
            Assert.That(options.IncludeIfNotExists, Is.False);
        }

        // ---------- ScriptAll: 설계 3.1의 부분 실패 허용 ----------

        private static ScriptTargetInfo Target(string schema, string type, string name)
            => new ScriptTargetInfo { Schema = schema, ObjectType = type, Name = name };

        [Test]
        public void ScriptAll_WritesOneFilePerObjectUsingTheSchemaTypeConvention()
        {
            var root = NewTempDir();
            try
            {
                var targets = new[]
                {
                    Target("dbo", "Table", "Users"),
                    Target("sales", "StoredProcedure", "usp_GetOrders")
                };

                var result = SmoManager.ScriptAll(targets, root, (t, outputPath) => File.WriteAllText(outputPath, $"-- {t.Name}"));

                Assert.That(result.SucceededCount, Is.EqualTo(2));
                Assert.That(result.FailedObjects, Is.Empty);
                Assert.That(File.Exists(Path.Combine(root, "dbo", "Tables", "Users.sql")), Is.True);
                Assert.That(File.Exists(Path.Combine(root, "sales", "StoredProcedures", "usp_GetOrders.sql")), Is.True);
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_ContinuesWithRemainingObjects_WhenOneObjectFails()
        {
            // 설계 3.1: "특정 객체 스크립팅 실패 시 해당 객체만 실패로 처리하고
            //           전체 스크립팅 프로세스가 중단되지 않도록"
            var root = NewTempDir();
            try
            {
                var targets = new[]
                {
                    Target("dbo", "Table", "Good1"),
                    Target("dbo", "Table", "Bad"),
                    Target("dbo", "Table", "Good2")
                };

                var result = SmoManager.ScriptAll(targets, root, (t, outputPath) =>
                {
                    if (t.Name == "Bad") throw new InvalidOperationException("scripting blew up");
                    File.WriteAllText(outputPath, $"-- {t.Name}");
                });

                Assert.That(result.SucceededCount, Is.EqualTo(2), "실패한 객체 이후의 객체도 계속 처리되어야 합니다");
                Assert.That(File.Exists(Path.Combine(root, "dbo", "Tables", "Good2.sql")), Is.True);
                Assert.That(result.FailedObjects, Is.EqualTo(new[] { "dbo.Bad" }));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_ReportsFailure_WhenEveryObjectFails()
        {
            var root = NewTempDir();
            try
            {
                var targets = new[] { Target("dbo", "Table", "Bad") };

                var result = SmoManager.ScriptAll(targets, root, (t, outputPath) => throw new InvalidOperationException("nope"));

                Assert.That(result.SucceededCount, Is.EqualTo(0));
                Assert.That(result.FailedObjects.Count, Is.EqualTo(1));
            }
            finally { TryDelete(root); }
        }

        // ---------- ScriptAll: 내용이 같으면 파일을 건드리지 않는다 ----------
        //
        // libgit2의 status는 인덱스에 기록된 stat 정보(크기·mtime)가 작업 트리와 일치하면
        // 파일 내용을 읽지 않는다. 내용이 같은데도 매번 덮어쓰면 그 캐시가 전부 무효화되어
        // status가 추적 파일 전부를 다시 해시하게 된다 — 객체 3000개 기준 18ms가 6.6초가 된다.

        [Test]
        public void ScriptAll_DoesNotTouchFile_WhenGeneratedContentIsIdentical()
        {
            var root = NewTempDir();
            try
            {
                var finalPath = Path.Combine(root, "dbo", "Tables", "Users.sql");
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.WriteAllText(finalPath, "CREATE TABLE [dbo].[Users]");

                var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(finalPath, stamp);

                var result = SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users") },
                    root,
                    (t, outputPath) => File.WriteAllText(outputPath, "CREATE TABLE [dbo].[Users]"));

                Assert.That(File.GetLastWriteTimeUtc(finalPath), Is.EqualTo(stamp),
                    "내용이 같으면 파일을 다시 쓰지 않아야 git 인덱스의 stat 캐시가 유지된다");
                Assert.That(result.SucceededCount, Is.EqualTo(1),
                    "쓰지 않았더라도 추출 자체는 성공으로 집계되어야 한다");
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_RewritesFile_WhenGeneratedContentDiffers()
        {
            var root = NewTempDir();
            try
            {
                var finalPath = Path.Combine(root, "dbo", "Tables", "Users.sql");
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.WriteAllText(finalPath, "CREATE TABLE [dbo].[Users] (Id int)");

                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users") },
                    root,
                    (t, outputPath) => File.WriteAllText(outputPath, "CREATE TABLE [dbo].[Users] (Id bigint)"));

                Assert.That(File.ReadAllText(finalPath), Is.EqualTo("CREATE TABLE [dbo].[Users] (Id bigint)"));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_PreservesBytesExactly_WhenContentDiffers()
        {
            // 인코딩이 바뀌면 내용이 같은 객체도 전부 "변경됨"으로 보인다.
            // SMO가 쓴 바이트를 그대로 옮겨야 업그레이드 직후 가짜 변경이 생기지 않는다.
            var root = NewTempDir();
            try
            {
                var expected = new byte[] { 0xFF, 0xFE, 0x43, 0x00, 0x52, 0x00 }; // UTF-16LE BOM + "CR"

                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users") },
                    root,
                    (t, outputPath) => File.WriteAllBytes(outputPath, expected));

                var finalPath = Path.Combine(root, "dbo", "Tables", "Users.sql");
                Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(expected));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_LeavesNoExtraFilesInRepository()
        {
            // 임시 파일이 작업 트리에 남으면 git이 미추적 파일로 잡아 목록을 오염시킨다.
            var root = NewTempDir();
            try
            {
                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users"), Target("dbo", "View", "vUsers") },
                    root,
                    (t, outputPath) => File.WriteAllText(outputPath, $"-- {t.Name}"));

                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(p => p.Substring(root.Length + 1).Replace('\\', '/'))
                    .OrderBy(p => p)
                    .ToArray();

                Assert.That(files, Is.EqualTo(new[] { "dbo/Tables/Users.sql", "dbo/Views/vUsers.sql" }));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_LeavesNoExtraFilesInRepository_WhenScriptingFails()
        {
            var root = NewTempDir();
            try
            {
                var result = SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Bad") },
                    root,
                    (t, outputPath) =>
                    {
                        File.WriteAllText(outputPath, "부분적으로 쓰다가");
                        throw new InvalidOperationException("scripting blew up");
                    });

                Assert.That(result.FailedObjects, Is.EqualTo(new[] { "dbo.Bad" }));
                Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories), Is.Empty,
                    "실패한 객체의 반쯤 쓰인 결과물이 작업 트리에 남으면 안 된다");
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_KeepsExistingFileIntact_WhenScriptingFails()
        {
            var root = NewTempDir();
            try
            {
                var finalPath = Path.Combine(root, "dbo", "Tables", "Users.sql");
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.WriteAllText(finalPath, "이전에 성공한 추출 결과");

                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users") },
                    root,
                    (t, outputPath) => throw new InvalidOperationException("nope"));

                Assert.That(File.ReadAllText(finalPath), Is.EqualTo("이전에 성공한 추출 결과"),
                    "추출에 실패했다고 직전에 성공한 파일을 잃어서는 안 된다");
            }
            finally { TryDelete(root); }
        }

        // ---------- 진행률과 취소 ----------
        //
        // 최초 온보딩은 객체 수에 비례해 길어진다(실측: 사용자 객체 200개 DB의 전체 추출 186초).
        // 그동안 화면이 아무 말도 하지 않으면 사용자는 멈춘 것과 구분할 수 없고,
        // 잘못 시작했을 때 되돌릴 방법도 없다.

        [Test]
        public void ScriptAll_ReportsProgressAfterEachObject()
        {
            var root = NewTempDir();
            try
            {
                var reported = new List<ExtractionProgress>();
                var progress = new ImmediateProgress(reported.Add);

                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "A"), Target("dbo", "View", "B") },
                    root,
                    (t, outputPath) => File.WriteAllText(outputPath, "-- x"),
                    progress);

                Assert.That(reported.Select(p => p.Completed), Is.EqualTo(new[] { 1, 2 }));
                Assert.That(reported.Select(p => p.Total), Is.EqualTo(new[] { 2, 2 }));
                Assert.That(reported[1].CurrentObject, Is.EqualTo("dbo.B"));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_ReportsProgressForFailedObjectsToo()
        {
            // 실패한 객체에서 진행이 멈춘 것처럼 보이면 사용자는 멈춘 줄 안다.
            var root = NewTempDir();
            try
            {
                var reported = new List<ExtractionProgress>();

                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Bad"), Target("dbo", "Table", "Good") },
                    root,
                    (t, outputPath) =>
                    {
                        if (t.Name == "Bad") throw new InvalidOperationException("nope");
                        File.WriteAllText(outputPath, "-- x");
                    },
                    new ImmediateProgress(reported.Add));

                Assert.That(reported.Select(p => p.Completed), Is.EqualTo(new[] { 1, 2 }));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_StopsImmediately_WhenCancelled()
        {
            var root = NewTempDir();
            try
            {
                using var cts = new CancellationTokenSource();
                var scripted = new List<string>();

                var targets = Enumerable.Range(0, 10)
                    .Select(i => Target("dbo", "Table", "T" + i))
                    .ToArray();

                Assert.Throws<OperationCanceledException>(() =>
                    SmoManager.ScriptAll(targets, root, (t, outputPath) =>
                    {
                        scripted.Add(t.Name!);
                        if (scripted.Count == 3) cts.Cancel();
                        File.WriteAllText(outputPath, "-- x");
                    }, null, cts.Token));

                Assert.That(scripted.Count, Is.EqualTo(3), "취소 이후로는 객체를 더 추출하지 않아야 한다");
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_KeepsAlreadyPublishedFiles_WhenCancelled()
        {
            // 취소는 되돌리기가 아니다. 이미 추출한 것을 지우면 다음 새로고침이 그만큼 다시 해야 한다.
            var root = NewTempDir();
            try
            {
                using var cts = new CancellationTokenSource();
                var count = 0;

                Assert.Throws<OperationCanceledException>(() =>
                    SmoManager.ScriptAll(
                        new[] { Target("dbo", "Table", "A"), Target("dbo", "Table", "B"), Target("dbo", "Table", "C") },
                        root,
                        (t, outputPath) =>
                        {
                            File.WriteAllText(outputPath, "-- x");
                            if (++count == 2) cts.Cancel();
                        }, null, cts.Token));

                Assert.That(File.Exists(Path.Combine(root, "dbo", "Tables", "A.sql")), Is.True);
                Assert.That(File.Exists(Path.Combine(root, "dbo", "Tables", "B.sql")), Is.True);
                Assert.That(File.Exists(Path.Combine(root, "dbo", "Tables", "C.sql")), Is.False);
            }
            finally { TryDelete(root); }
        }

        /// <summary>보고를 그 자리에서 그대로 전달한다. Progress&lt;T&gt;는 스레드 풀로 넘겨 순서와 시점이 흔들린다.</summary>
        private sealed class ImmediateProgress : IProgress<ExtractionProgress>
        {
            private readonly Action<ExtractionProgress> _onReport;
            public ImmediateProgress(Action<ExtractionProgress> onReport) { _onReport = onReport; }
            public void Report(ExtractionProgress value) => _onReport(value);
        }

        // ---------- 스크립팅 옵션 ----------

        [Test]
        public void BuildScriptingOptions_EnablesConstraintsIndexesAndExtendedProperties()
        {
            // 이 값들이 꺼져 있으면 테이블 .sql에 컬럼 정의만 남는다. 기본값 제약도,
            // 기본 키도, 인덱스도 없는 파일로는 배포 스크립트가 테이블을 재생산하지 못한다.
            var options = SmoManager.BuildScriptingOptions();

            Assert.Multiple(() =>
            {
                Assert.That(options.DriAll, Is.True, "기본값·PK·FK·UNIQUE·CHECK");
                Assert.That(options.Indexes, Is.True);
                Assert.That(options.ClusteredIndexes, Is.True);
                Assert.That(options.NonClusteredIndexes, Is.True);
                Assert.That(options.XmlIndexes, Is.True);
                Assert.That(options.FullTextIndexes, Is.True);
                Assert.That(options.ExtendedProperties, Is.True);
            });
        }

        [Test]
        public void BuildScriptingOptions_LeavesEnvironmentSpecificArtifactsOut()
        {
            // 끄는 쪽도 계약이다. 권한은 서버마다 주체가 달라 저장소를 환경 종속으로 만들고,
            // 통계는 데이터 분포의 부산물이라 같은 스키마에서도 매번 달라져 잡음 diff가 된다.
            var options = SmoManager.BuildScriptingOptions();

            Assert.Multiple(() =>
            {
                Assert.That(options.Permissions, Is.False);
                Assert.That(options.Statistics, Is.False);
                Assert.That(options.ScriptData, Is.False);
                Assert.That(options.ScriptDrops, Is.False);
                Assert.That(options.IncludeIfNotExists, Is.False);
            });
        }

        // ---------- 객체 필터 ----------

        [Test]
        public void ShouldInclude_IncludesEverything_WhenNoFilterGiven()
        {
            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Users"), null), Is.True);
        }

        [Test]
        public void ShouldInclude_MatchesSchemaQualifiedName()
        {
            var filter = SmoManager.BuildFilter(new List<string> { "dbo.Users" });

            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Users"), filter), Is.True);
            Assert.That(SmoManager.ShouldInclude(Target("app", "Table", "Users"), filter), Is.False,
                "스키마가 다른 동명 객체를 구분해야 합니다");
        }

        [Test]
        public void ShouldInclude_MatchesUnqualifiedNameForConvenience()
        {
            var filter = SmoManager.BuildFilter(new List<string> { "Users" });

            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Users"), filter), Is.True);
            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Orders"), filter), Is.False);
        }

        [Test]
        public void ShouldInclude_IsCaseInsensitive()
        {
            var filter = SmoManager.BuildFilter(new List<string> { "DBO.USERS" });
            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Users"), filter), Is.True);
        }

        private static string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_smo_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, true); } catch { }
            }
        }
    }
}


