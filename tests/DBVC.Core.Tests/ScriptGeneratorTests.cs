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

            Assert.That(script, Does.Contain("DBVC Deployment Script"));
            Assert.That(script, Does.Contain("Objects: 2"));
            Assert.That(script, Does.Contain("2026-08-01"));
        }

        [Test]
        public void BuildScript_LabelsRollbackScriptsDistinctly()
        {
            var script = ScriptGenerator.BuildScript(
                new[] { Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);") },
                ScriptKind.Rollback,
                GeneratedAt);

            Assert.That(script, Does.Contain("DBVC Rollback Script"));
            Assert.That(script, Does.Not.Contain("Deployment"));
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
            Assert.That(script, Does.Contain("Objects: 1"), "헤더의 개수는 실제로 포함된 객체 수여야 합니다");
        }

        [Test]
        public void BuildScript_HandlesNullSectionList()
        {
            Assert.That(ScriptGenerator.BuildScript(null, ScriptKind.Deployment, GeneratedAt), Does.Contain("Objects: 0"));
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
                Is.EqualTo(ScriptGenerator.BuildScript(sections.Reverse().ToArray(), ScriptKind.Deployment, GeneratedAt)));
        }

        private static int IndexOf(string script, string needle)
        {
            var index = script.IndexOf(needle, StringComparison.Ordinal);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), $"'{needle}'이(가) 스크립트에 없습니다");
            return index;
        }
    }
}
