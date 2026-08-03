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
        public void IsAvailable_SkipsBlankPathEntries_WithoutProbingTheCurrentDirectory()
        {
            // 빈 PATH 항목은 셸 관례상 "현재 작업 디렉터리"를 뜻한다. 건너뛰지 않으면
            // Path.Combine("", "ssh.exe")가 "ssh.exe"가 되어 fileExists가 프로세스의
            // 작업 디렉터리를 실제로 확인하게 된다 - 이는 사용자 PATH와 무관한 오탐이다.
            var probedPaths = new List<string>();
            bool RecordingFileExists(string path)
            {
                probedPaths.Add(path);
                return false;
            }

            var pathVariable = Path.PathSeparator + "" + Path.PathSeparator + " " + Path.PathSeparator;

            var found = SshExecutableLocator.IsAvailable(null, null, pathVariable, RecordingFileExists);

            Assert.That(found, Is.False);
            Assert.That(probedPaths, Is.Empty,
                "빈/공백 PATH 항목에 대해서는 fileExists가 전혀 호출되지 않아야 한다 - " +
                "호출된다면 현재 작업 디렉터리를 오탐 대상으로 확인하고 있다는 뜻이다");
        }
    }
}
