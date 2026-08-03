using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SshExecutableLocatorTests
    {
        private static Func<string, bool> NothingExists => _ => false;

        private static Func<string, bool> Exists(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        [Test]
        public void IsAvailable_TrustsGitSshCommand_WithoutSearchingPath()
        {
            Assert.That(
                SshExecutableLocator.IsAvailable("ssh -o StrictHostKeyChecking=yes", null, null, NothingExists),
                Is.True,
                "사용자가 GIT_SSH_COMMAND를 설정했다면 libgit2가 그것을 씁니다. 내용을 검증하지 않습니다");
        }

        [Test]
        public void IsAvailable_TrustsGitSsh_WithoutSearchingPath()
        {
            Assert.That(
                SshExecutableLocator.IsAvailable(null, @"C:\Program Files\PuTTY\plink.exe", null, NothingExists),
                Is.True,
                "PuTTY plink를 GIT_SSH로 지정한 환경을 놓치면 안 됩니다");
        }

        [TestCase("")]
        [TestCase("   ")]
        public void IsAvailable_IgnoresBlankEnvironmentVariables(string blank)
        {
            Assert.That(SshExecutableLocator.IsAvailable(blank, blank, null, NothingExists), Is.False);
        }

        [Test]
        public void IsAvailable_FindsTheExecutableOnPath()
        {
            var dir = Path.Combine("usr", "bin");
            var pathVariable = string.Join(Path.PathSeparator.ToString(), new[] { Path.Combine("nope"), dir });

            var found = SshExecutableLocator.IsAvailable(
                null, null, pathVariable,
                Exists(Path.Combine(dir, "ssh"), Path.Combine(dir, "ssh.exe")));

            Assert.That(found, Is.True);
        }

        [Test]
        public void IsAvailable_ReturnsFalse_WhenPathHasNoSshExecutable()
        {
            var pathVariable = string.Join(Path.PathSeparator.ToString(), new[] { "a", "b" });

            Assert.That(SshExecutableLocator.IsAvailable(null, null, pathVariable, NothingExists), Is.False);
        }

        [Test]
        public void IsAvailable_ReturnsFalse_WhenNothingIsConfigured()
        {
            Assert.That(SshExecutableLocator.IsAvailable(null, null, null, NothingExists), Is.False);
        }

        [Test]
        public void IsAvailable_ToleratesEmptyPathEntries()
        {
            var pathVariable = Path.PathSeparator + "" + Path.PathSeparator;

            Assert.DoesNotThrow(() => SshExecutableLocator.IsAvailable(null, null, pathVariable, NothingExists));
        }
    }
}
