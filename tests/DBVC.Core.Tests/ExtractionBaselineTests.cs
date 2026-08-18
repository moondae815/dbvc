using System;
using System.IO;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 변경분만 추출하려면 "이미 추출된 기준선이 있는가"를 먼저 알아야 한다.
    /// 기준선이 없는데 변경분만 추출하면 저장소가 빈 채로 남고 사용자는 아무것도 커밋할 수 없다.
    /// </summary>
    [TestFixture]
    public class ExtractionBaselineTests
    {
        [Test]
        public void Exists_IsFalse_ForAnEmptyRepository()
        {
            var root = NewTempDir();
            try
            {
                Assert.That(ExtractionBaseline.Exists(root), Is.False);
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void Exists_IsTrue_WhenAConventionShapedScriptExists()
        {
            var root = NewTempDir();
            try
            {
                Write(root, "dbo/Tables/Users.sql");
                Assert.That(ExtractionBaseline.Exists(root), Is.True);
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void Exists_IsFalse_WhenTheOnlyScriptsAreNotDbvcExtracts()
        {
            // 사용자가 손으로 넣어 둔 .sql은 DBVC의 추출 기준선이 아니다.
            // 이것을 기준선으로 인정하면 전체 추출이 건너뛰어져 저장소가 비어 있는 채로 남는다.
            var root = NewTempDir();
            try
            {
                Write(root, "migrations/001-init.sql");
                Write(root, "readme.sql");
                Assert.That(ExtractionBaseline.Exists(root), Is.False);
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void Exists_IsFalse_WhenTheRepositoryPathDoesNotExist()
        {
            Assert.That(ExtractionBaseline.Exists(Path.Combine(Path.GetTempPath(), "dbvc_missing_" + Guid.NewGuid().ToString("N"))), Is.False);
        }

        [Test]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Exists_IsFalse_ForMissingPaths(string? path)
        {
            Assert.That(ExtractionBaseline.Exists(path!), Is.False);
        }

        private static void Write(string root, string relativePath)
        {
            var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "-- script");
        }

        private static string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_baseline_" + Guid.NewGuid().ToString("N"));
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
