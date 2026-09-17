using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 배포·감사 클론의 병합 경로. bare origin과 그것을 받은 고정 클론, 원격에 브랜치를 올리는
    /// 작성자 클론 셋으로 팀의 실제 배치를 흉내 낸다.
    /// </summary>
    [TestFixture]
    public class GitManagerMergeTests
    {
        private const string Server = "localhost";
        private const string Database = "testdb";
        private const string SqlPath = "dbo/StoredProcedures/usp_Order.sql";

        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void CleanUp()
        {
            foreach (var dir in _tempDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(dir, true);
                }
                catch { }
            }
            _tempDirs.Clear();
        }

        private string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_merge_" + Guid.NewGuid().ToString("N"));
            _tempDirs.Add(path);
            return path;
        }

        private static Signature Sig(string name = "Author") =>
            new Signature(name, name.ToLowerInvariant() + "@example.com", DateTimeOffset.Now);

        private static void WriteFile(string repoPath, string relativePath, string content)
        {
            var full = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        /// <summary>
        /// bare origin에 master·develop을 같은 첫 커밋에 두고 target을 받은 클론을 만든다.
        /// init.defaultBranch 전역 설정에 기대지 않도록 두 이름을 origin에 직접 만든다.
        /// </summary>
        private (string LocalPath, string OriginPath) NewPinnedClone(string target, MappingMode mode = MappingMode.Deploy)
        {
            var seed = NewTempDir();
            Repository.Init(seed);
            WriteFile(seed, SqlPath, "CREATE OR ALTER PROCEDURE dbo.usp_Order AS\nSELECT 1\n");
            using (var repo = new Repository(seed))
            {
                Commands.Stage(repo, "*");
                repo.Commit("initial", Sig(), Sig());
            }

            var origin = NewTempDir();
            Repository.Clone(seed, origin, new CloneOptions { IsBare = true });
            using (var repo = new Repository(origin))
            {
                var tip = repo.Head.Tip;
                if (repo.Branches[EnvironmentBranches.Master] == null) repo.CreateBranch(EnvironmentBranches.Master, tip);
                if (repo.Branches[EnvironmentBranches.Develop] == null) repo.CreateBranch(EnvironmentBranches.Develop, tip);
            }

            var local = NewTempDir();
            Repository.Clone(origin, local, new CloneOptions { BranchName = target });
            GitIdentity.Write(local, "Deployer", "deployer@example.com");
            return (local, origin);
        }

        private string PushAuthorBranch(string originPath, string branch, string basedOn, string relativePath, string content)
        {
            var author = NewTempDir();
            Repository.Clone(originPath, author, new CloneOptions { BranchName = basedOn });
            using var repo = new Repository(author);
            var created = repo.Branches[branch] ?? repo.CreateBranch(branch);
            Commands.Checkout(repo, created);
            WriteFile(author, relativePath, content);
            Commands.Stage(repo, "*");
            var commit = repo.Commit(branch + " 변경", Sig(), Sig());
            repo.Network.Push(repo.Network.Remotes["origin"], $"refs/heads/{branch}:refs/heads/{branch}");
            return commit.Sha;
        }

        private GitManager NewPinnedGitManager(string localPath, string target, MappingMode mode)
        {
            var config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            config.AddMapping(new MappingConfig
            {
                ServerName = Server, DatabaseName = Database, GitPath = localPath, Branch = target, Mode = mode
            });
            return new GitManager(config);
        }

        // ---------- GetUnmergedBranches ----------

        [Test]
        public void GetUnmergedBranches_ExcludesMergedAndEnvironmentBranches()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, "CREATE OR ALTER PROCEDURE dbo.usp_Order AS\nSELECT 2\n");
            // 끝 커밋이 master 첫 커밋 그대로인 브랜치 - develop에 이미 들어 있다.
            using (var repo = new Repository(origin)) repo.CreateBranch("PROJ-0", repo.Branches[EnvironmentBranches.Master].Tip);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var names = git.GetUnmergedBranches(Server, Database).Select(b => b.Name).ToList();

            Assert.That(names, Is.EqualTo(new[] { "PROJ-1" }));
        }

        [Test]
        public void GetUnmergedBranches_FetchesFirst_WhenBranchWasPushedAfterClone()
        {
            // 낡은 목록을 최신인 척 보여 주지 않는다.
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            PushAuthorBranch(origin, "PROJ-2", EnvironmentBranches.Master, SqlPath, "x\n");

            var branch = git.GetUnmergedBranches(Server, Database).Single();

            Assert.That(branch.Name, Is.EqualTo("PROJ-2"));
            Assert.That(branch.CommitCount, Is.EqualTo(1));
            Assert.That(branch.LastCommitAuthor, Is.EqualTo("Author"));
            Assert.That(branch.IsInDevelop, Is.Null, "목적지가 develop이면 테스트 반영 열이 없습니다");
        }

        [Test]
        public void GetUnmergedBranches_MarksIsInDevelop_WhenTargetIsMaster()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Master, MappingMode.Audit);
            var tested = PushAuthorBranch(origin, "PROJ-3", EnvironmentBranches.Master, SqlPath, "tested\n");
            PushAuthorBranch(origin, "PROJ-4", EnvironmentBranches.Master, "dbo/Views/v_A.sql", "untested\n");
            using (var repo = new Repository(origin))
            {
                // develop이 PROJ-3을 담은 것처럼 ref를 옮긴다. 병합 커밋이 없어도 조상 판정은 같다.
                repo.Refs.UpdateTarget("refs/heads/develop", tested);
            }
            var git = NewPinnedGitManager(local, EnvironmentBranches.Master, MappingMode.Audit);

            var branches = git.GetUnmergedBranches(Server, Database).ToDictionary(b => b.Name);

            Assert.That(branches["PROJ-3"].IsInDevelop, Is.True);
            Assert.That(branches["PROJ-4"].IsInDevelop, Is.False);
            Assert.That(branches.ContainsKey(EnvironmentBranches.Develop), Is.False);
        }

        [Test]
        public void GetUnmergedBranches_ReturnsEmpty_WhenMappingIsNotPinned()
        {
            var (local, _) = NewPinnedClone(EnvironmentBranches.Develop);
            var config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            config.AddMapping(Server, Database, local);

            Assert.That(new GitManager(config).GetUnmergedBranches(Server, Database), Is.Empty);
        }
    }
}
