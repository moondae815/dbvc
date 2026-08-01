using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class WorkingTreeCleanerTests
    {
        private string _repoPath = null!;

        [SetUp]
        public void SetUp()
        {
            _repoPath = Path.Combine(Path.GetTempPath(), "dbvc_clean_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_repoPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (!Directory.Exists(_repoPath)) return;
            try { Directory.Delete(_repoPath, true); } catch { }
        }

        private string WriteFile(string relativePath, string content = "CREATE TABLE Users (Id INT);")
        {
            var full = Path.Combine(_repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return full;
        }

        private static ChangeRecord Record(string state, string relativePath, long lastLogId)
            => new ChangeRecord
            {
                Schema = "dbo",
                ObjectName = "Users",
                State = state,
                QualifiedName = "dbo.Users",
                RelativePath = relativePath,
                LastLogId = lastLogId
            };

        // ---------- 삭제 대상 ----------

        [Test]
        public void RemoveDeletedObjectFiles_DeletesTheFile_WhenADdlLogRowBacksTheDeletion()
        {
            var full = WriteFile("dbo/Tables/Users.sql");

            var result = new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", "dbo/Tables/Users.sql", 7) });

            Assert.That(File.Exists(full), Is.False,
                "파일이 남으면 Git이 삭제를 감지하지 못해 커밋되지 않습니다");
            Assert.That(result.RemovedPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
            Assert.That(result.HasFailures, Is.False);
        }

        [Test]
        public void RemoveDeletedObjectFiles_IsCaseInsensitiveForTheState()
        {
            var full = WriteFile("dbo/Tables/Users.sql");

            new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("deleted", "dbo/Tables/Users.sql", 7) });

            Assert.That(File.Exists(full), Is.False);
        }

        // ---------- 건드리면 안 되는 것 ----------

        [Test]
        public void RemoveDeletedObjectFiles_LeavesTheFile_WhenNoDdlLogRowBacksTheDeletion()
        {
            var full = WriteFile("dbo/Tables/Users.sql");

            var result = new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", "dbo/Tables/Users.sql", 0) });

            Assert.That(File.Exists(full), Is.True,
                "LastLogId가 0이면 Git 상태에서만 유래한 항목이라 지울 근거가 없습니다");
            Assert.That(result.RemovedPaths, Is.Empty);
        }

        [TestCase("Modified")]
        [TestCase("Added")]
        public void RemoveDeletedObjectFiles_LeavesTheFile_ForStatesOtherThanDeleted(string state)
        {
            var full = WriteFile("dbo/Tables/Users.sql");

            new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record(state, "dbo/Tables/Users.sql", 7) });

            Assert.That(File.Exists(full), Is.True);
        }

        [TestCase("notes.txt")]
        [TestCase("dbo/Tables/extra/Users.sql")]
        [TestCase("Users.sql")]
        public void RemoveDeletedObjectFiles_LeavesFilesThatDoNotFollowThePathConvention(string relativePath)
        {
            var full = WriteFile(relativePath);

            new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", relativePath, 7) });

            Assert.That(File.Exists(full), Is.True,
                "규약에 맞지 않는 경로는 DBVC가 만든 파일이 아닙니다");
        }

        [Test]
        public void RemoveDeletedObjectFiles_NeverEscapesTheRepositoryRoot()
        {
            // 저장소를 한 단계 깊이 두고 그 형제 폴더에 희생양을 만든다.
            // 그래야 "../Tables/Secret.sql"이 실제로 그 파일을 가리킨다.
            var outer = Path.Combine(Path.GetTempPath(), "dbvc_escape_" + Guid.NewGuid().ToString("N"));
            var repo = Path.Combine(outer, "repo");
            var victim = Path.Combine(outer, "Tables", "Secret.sql");
            Directory.CreateDirectory(repo);
            Directory.CreateDirectory(Path.GetDirectoryName(victim)!);
            File.WriteAllText(victim, "secret");

            try
            {
                // ".." 세 조각도 경로 규약 검사는 통과하므로 루트 검사가 마지막 방어선이다.
                var result = new WorkingTreeCleaner()
                    .RemoveDeletedObjectFiles(repo, new[] { Record("Deleted", "../Tables/Secret.sql", 7) });

                Assert.That(File.Exists(victim), Is.True, "저장소 밖의 파일을 지워서는 안 됩니다");
                Assert.That(result.RemovedPaths, Is.Empty);
            }
            finally
            {
                try { Directory.Delete(outer, true); } catch { }
            }
        }

        // ---------- 무해한 입력 ----------

        [Test]
        public void RemoveDeletedObjectFiles_DoesNothing_WhenTheFileIsAlreadyGone()
        {
            var result = new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", "dbo/Tables/Gone.sql", 7) });

            Assert.That(result.RemovedPaths, Is.Empty);
            Assert.That(result.HasFailures, Is.False);
        }

        [Test]
        public void RemoveDeletedObjectFiles_LeavesTheDirectoryInPlace()
        {
            WriteFile("dbo/Tables/Users.sql");

            new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", "dbo/Tables/Users.sql", 7) });

            Assert.That(Directory.Exists(Path.Combine(_repoPath, "dbo", "Tables")), Is.True,
                "Git은 빈 디렉터리를 추적하지 않으므로 지울 이유가 없습니다");
        }

        [Test]
        public void RemoveDeletedObjectFiles_ReturnsEmpty_ForAMissingRepositoryPath()
        {
            var result = new WorkingTreeCleaner().RemoveDeletedObjectFiles(
                Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N")),
                new[] { Record("Deleted", "dbo/Tables/Users.sql", 7) });

            Assert.That(result.RemovedPaths, Is.Empty);
            Assert.That(result.HasFailures, Is.False);
        }

        [Test]
        public void RemoveDeletedObjectFiles_ToleratesNullCollectionAndNullRecords()
        {
            var cleaner = new WorkingTreeCleaner();

            Assert.DoesNotThrow(() => cleaner.RemoveDeletedObjectFiles(_repoPath, null!));
            Assert.DoesNotThrow(() => cleaner.RemoveDeletedObjectFiles(_repoPath, new ChangeRecord?[] { null }!));
        }

        // ---------- 실패 격리 ----------

        [Test]
        [Platform("Win", Reason = "읽기 전용 파일의 삭제가 거부되는 것은 Windows 동작입니다")]
        public void RemoveDeletedObjectFiles_IsolatesAFailure_AndKeepsProcessingTheRest()
        {
            var locked = WriteFile("dbo/Tables/Locked.sql");
            var deletable = WriteFile("dbo/Tables/Users.sql");
            File.SetAttributes(locked, FileAttributes.ReadOnly);

            try
            {
                var result = new WorkingTreeCleaner().RemoveDeletedObjectFiles(_repoPath, new[]
                {
                    Record("Deleted", "dbo/Tables/Locked.sql", 7),
                    Record("Deleted", "dbo/Tables/Users.sql", 8)
                });

                Assert.That(result.FailedPaths, Is.EqualTo(new[] { "dbo/Tables/Locked.sql" }));
                Assert.That(result.RemovedPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }),
                    "하나의 실패가 나머지 정리를 막아서는 안 됩니다");
                Assert.That(result.HasFailures, Is.True);
                Assert.That(File.Exists(deletable), Is.False);
            }
            finally
            {
                File.SetAttributes(locked, FileAttributes.Normal);
            }
        }
    }
}
