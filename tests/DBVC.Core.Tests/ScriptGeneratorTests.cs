using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class ScriptGeneratorTests
    {
        private static readonly DateTimeOffset GeneratedAt = new DateTimeOffset(2026, 8, 1, 9, 30, 0, TimeSpan.Zero);

        private static ScriptSection Section(string qualifiedName, string relativePath, string sql)
            => new ScriptSection { QualifiedName = qualifiedName, RelativePath = relativePath, Sql = sql };

        private static string Build(params ScriptSection[] sections)
            => ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, GeneratedAt);

        // ---------- 헤더 ----------

        [Test]
        public void BuildScript_WritesHeaderWithKindAndObjectCount()
        {
            var script = Build(
                Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);"),
                Section("dbo.Orders", "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);"));

            Assert.That(script, Does.Contain("DBVC 배포 스크립트"));
            Assert.That(script, Does.Contain("객체: 2"));
            Assert.That(script, Does.Contain("2026-08-01"));
        }

        [Test]
        public void BuildScript_LabelsRollbackScriptsDistinctly()
        {
            var script = ScriptGenerator.BuildScript(
                new[] { Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);") },
                ScriptKind.Rollback,
                GeneratedAt);

            Assert.That(script, Does.Contain("DBVC 롤백 스크립트"));
            Assert.That(script, Does.Not.Contain("배포"));
        }

        [Test]
        public void BuildScript_RecordsExcludedObjectsInTheHeader()
        {
            var script = ScriptGenerator.BuildScript(
                new[] { Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);") },
                ScriptKind.Rollback,
                GeneratedAt,
                new[]
                {
                    new ScriptExclusion("dbo.Gone", ScriptExclusionReason.NoContent),
                    new ScriptExclusion("dbo.AlsoGone", ScriptExclusionReason.NoContent)
                });

            Assert.That(script, Does.Contain("객체: 1"));
            Assert.That(script, Does.Contain("제외 — 스크립트로 만들 내용이 없습니다: 2 (dbo.Gone, dbo.AlsoGone)"),
                "알림은 닫으면 사라지지만 헤더는 파일과 함께 남습니다");
        }

        [Test]
        public void BuildScript_OmitsTheExcludedLine_WhenNothingWasExcluded()
        {
            var withNull = Build(Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);"));
            var withEmpty = ScriptGenerator.BuildScript(
                new[] { Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);") },
                ScriptKind.Deployment,
                GeneratedAt,
                Array.Empty<ScriptExclusion>());

            Assert.That(withNull, Does.Not.Contain("제외"),
                "인자를 생략한 기존 호출부의 출력이 달라지면 안 됩니다");
            Assert.That(withEmpty, Does.Not.Contain("제외"));
            Assert.That(withEmpty, Is.EqualTo(withNull),
                "빈 목록과 null은 같은 결과를 내야 합니다");
        }

        [Test]
        public void BuildScript_WritesTheHeaderInKorean()
        {
            // 스크립트는 사람이 열어 보는 산출물이다. 사유만 한국어로 적으면 한 헤더에
            // 두 언어가 섞인다.
            var sections = new[]
            {
                new ScriptSection { QualifiedName = "dbo.GetUser", RelativePath = "dbo/StoredProcedures/GetUser.sql", Sql = "CREATE OR ALTER PROCEDURE dbo.GetUser AS SELECT 1" }
            };

            var script = ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, GeneratedAt);

            Assert.That(script, Does.Contain("DBVC 배포 스크립트"));
            Assert.That(script, Does.Contain("생성:"));
            Assert.That(script, Does.Contain("객체: 1"));
            Assert.That(script, Does.Not.Contain("Deployment Script"));
        }

        [Test]
        public void BuildScript_WritesRollbackTitleInKorean()
        {
            var sections = new[]
            {
                new ScriptSection { QualifiedName = "dbo.A", RelativePath = "dbo/Views/A.sql", Sql = "CREATE VIEW dbo.A AS SELECT 1" }
            };

            var script = ScriptGenerator.BuildScript(sections, ScriptKind.Rollback, GeneratedAt);

            Assert.That(script, Does.Contain("DBVC 롤백 스크립트"));
        }

        [Test]
        public void BuildScript_GroupsExclusionsByReason()
        {
            // 셋을 한 줄에 뭉치면 사용자가 무엇을 손으로 해야 하는지 알 수 없다.
            var sections = new[]
            {
                new ScriptSection { QualifiedName = "dbo.A", RelativePath = "dbo/Views/A.sql", Sql = "CREATE VIEW dbo.A AS SELECT 1" }
            };
            var exclusions = new[]
            {
                new ScriptExclusion("dbo.Orders", ScriptExclusionReason.ManualChangeRequired),
                new ScriptExclusion("dbo.Customers", ScriptExclusionReason.ManualChangeRequired),
                new ScriptExclusion("dbo.Temp1", ScriptExclusionReason.NotInBranch)
            };

            var script = ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, GeneratedAt, exclusions);

            Assert.That(script, Does.Contain("수동 변경이 필요합니다: 2 (dbo.Orders, dbo.Customers)"));
            Assert.That(script, Does.Contain("확인이 필요합니다: 1 (dbo.Temp1)"));
        }

        [Test]
        public void BuildScript_OmitsExclusionLines_WhenNothingWasExcluded()
        {
            var sections = new[]
            {
                new ScriptSection { QualifiedName = "dbo.A", RelativePath = "dbo/Views/A.sql", Sql = "CREATE VIEW dbo.A AS SELECT 1" }
            };

            var script = ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, GeneratedAt);

            Assert.That(script, Does.Not.Contain("제외"));
        }

        // ---------- 섹션 ----------

        [Test]
        public void BuildScript_EmitsAHeaderCommentPerObject()
        {
            var script = Build(Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);"));

            Assert.That(script, Does.Contain("dbo.Users"));
            Assert.That(script, Does.Contain("dbo/Tables/Users.sql"));
        }

        [Test]
        public void BuildScript_SeparatesSectionsWithAStandaloneGoBatchTerminator()
        {
            var script = Build(
                Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);"),
                Section("dbo.Orders", "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);"));

            var goLines = script.Split('\n').Count(line => line.Trim() == "GO");
            Assert.That(goLines, Is.EqualTo(2), "각 객체 뒤에 배치 구분자가 하나씩 있어야 합니다");
        }

        [Test]
        public void BuildScript_DoesNotAddASecondGo_WhenTheSourceAlreadyEndsWithOne()
        {
            var script = Build(Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);\nGO\n"));

            var goLines = script.Split('\n').Count(line => line.Trim() == "GO");
            Assert.That(goLines, Is.EqualTo(1));
        }

        [Test]
        public void BuildScript_SkipsSectionsWithNoSql()
        {
            var script = Build(
                Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);"),
                Section("dbo.Empty", "dbo/Tables/Empty.sql", "   "));

            Assert.That(script, Does.Contain("dbo.Users"));
            Assert.That(script, Does.Not.Contain("dbo.Empty"));
            Assert.That(script, Does.Contain("객체: 1"), "헤더의 개수는 실제로 포함된 객체 수여야 합니다");
        }

        [Test]
        public void BuildScript_HandlesNullSectionList()
        {
            Assert.That(ScriptGenerator.BuildScript(null, ScriptKind.Deployment, GeneratedAt), Does.Contain("객체: 0"));
        }

        // ---------- 정렬 ----------

        [Test]
        public void BuildScript_OrdersByObjectTypeGroup_SoTypesAndTablesPrecedeProcedures()
        {
            var script = Build(
                Section("dbo.usp_Get", "dbo/StoredProcedures/usp_Get.sql", "CREATE PROCEDURE usp_Get AS SELECT 1;"),
                Section("dbo.vw_All", "dbo/Views/vw_All.sql", "CREATE VIEW vw_All AS SELECT 1 AS X;"),
                Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);"),
                Section("dbo.AddressType", "dbo/Types/AddressType.sql", "CREATE TYPE AddressType FROM NVARCHAR(50);"));

            Assert.That(IndexOf(script, "dbo.AddressType"), Is.LessThan(IndexOf(script, "dbo.Users")));
            Assert.That(IndexOf(script, "dbo.Users"), Is.LessThan(IndexOf(script, "dbo.vw_All")));
            Assert.That(IndexOf(script, "dbo.vw_All"), Is.LessThan(IndexOf(script, "dbo.usp_Get")));
        }

        [Test]
        public void BuildScript_OrdersAlphabeticallyWithinTheSameObjectTypeGroup()
        {
            var script = Build(
                Section("dbo.Zebra", "dbo/Tables/Zebra.sql", "CREATE TABLE Zebra (Id INT);"),
                Section("dbo.Apple", "dbo/Tables/Apple.sql", "CREATE TABLE Apple (Id INT);"));

            Assert.That(IndexOf(script, "dbo.Apple"), Is.LessThan(IndexOf(script, "dbo.Zebra")));
        }

        [Test]
        public void BuildScript_IsDeterministicForTheSameInput()
        {
            var sections = new[]
            {
                Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);"),
                Section("dbo.Orders", "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);")
            };

            Assert.That(ScriptGenerator.BuildScript(sections, ScriptKind.Deployment, GeneratedAt),
                Is.EqualTo(ScriptGenerator.BuildScript(Enumerable.Reverse(sections).ToArray(), ScriptKind.Deployment, GeneratedAt)));
        }

        private static int IndexOf(string script, string needle)
        {
            var index = script.IndexOf(needle, StringComparison.Ordinal);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), $"'{needle}'이(가) 스크립트에 없습니다");
            return index;
        }
    }
}
