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
        public void GetUnmergedBranches_DropsBranch_WhenDeletedOnRemote()
        {
            // GitLab에서 지운 브랜치가 목록에 남으면 병합으로 되살릴 수 있다.
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, "x\n");
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            Assert.That(git.GetUnmergedBranches(Server, Database).Select(b => b.Name), Does.Contain("PROJ-1"));
            using (var repo = new Repository(origin)) repo.Branches.Remove("PROJ-1");

            var names = git.GetUnmergedBranches(Server, Database).Select(b => b.Name).ToList();

            Assert.That(names, Does.Not.Contain("PROJ-1"));
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

        // ---------- PreviewMerge ----------

        private static string Proc(params string[] bodyLines) =>
            "CREATE OR ALTER PROCEDURE dbo.usp_Order AS\n" + string.Join("\n", bodyLines) + "\n";

        [Test]
        public void PreviewMerge_ListsChangedPaths_WithoutTouchingWorkingTree()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);
            string headBefore;
            using (var repo = new Repository(local)) headBefore = repo.Head.Tip.Sha;

            var preview = git.PreviewMerge(Server, Database, "PROJ-1");

            Assert.That(preview.ChangedPaths, Is.EqualTo(new[] { SqlPath }));
            Assert.That(preview.ConflictPaths, Is.Empty);
            Assert.That(preview.AlreadyMerged, Is.False);
            using var after = new Repository(local);
            Assert.That(after.Head.Tip.Sha, Is.EqualTo(headBefore));
            Assert.That(after.RetrieveStatus().IsDirty, Is.False);
        }

        [Test]
        public void PreviewMerge_ReportsConflicts_WithoutTouchingWorkingTree()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-A", EnvironmentBranches.Master, SqlPath, Proc("SELECT 'A'"));
            PushAuthorBranch(origin, "PROJ-B", EnvironmentBranches.Master, SqlPath, Proc("SELECT 'B'"));
            using (var repo = new Repository(origin))
            {
                repo.Refs.UpdateTarget("refs/heads/develop", repo.Branches["PROJ-A"].Tip.Sha);
            }
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);

            var preview = git.PreviewMerge(Server, Database, "PROJ-B");

            Assert.That(preview.ConflictPaths, Is.EqualTo(new[] { SqlPath }));
            using var after = new Repository(local);
            Assert.That(after.RetrieveStatus().IsDirty, Is.False);
        }

        [Test]
        public void PreviewMerge_ReportsAlreadyMerged_WhenSourceTipIsInTarget()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            using (var repo = new Repository(origin)) repo.CreateBranch("PROJ-0", repo.Branches[EnvironmentBranches.Develop].Tip);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);

            Assert.That(git.PreviewMerge(Server, Database, "PROJ-0").AlreadyMerged, Is.True);
        }

        [Test]
        public void PreviewMerge_ReportsLeaks_OnlyWhenTargetIsMaster()
        {
            // PROJ-B의 스냅샷에 PROJ-A의 줄이 딸려 온 상황. 두 클론 모두 같은 origin을 본다.
            var carried = "DECLARE @DiscountRate DECIMAL(5,2) = 0.1";
            var (masterLocal, origin) = NewPinnedClone(EnvironmentBranches.Master, MappingMode.Audit);
            PushAuthorBranch(origin, "PROJ-A", EnvironmentBranches.Master, SqlPath, Proc(carried, "SELECT 1"));
            PushAuthorBranch(origin, "PROJ-B", EnvironmentBranches.Master, SqlPath, Proc(carried, "SELECT 1", "SELECT 'B'"));

            var auditGit = NewPinnedGitManager(masterLocal, EnvironmentBranches.Master, MappingMode.Audit);
            auditGit.GetUnmergedBranches(Server, Database);
            var masterPreview = auditGit.PreviewMerge(Server, Database, "PROJ-B");

            var developLocal = NewTempDir();
            Repository.Clone(origin, developLocal, new CloneOptions { BranchName = EnvironmentBranches.Develop });
            var deployGit = NewPinnedGitManager(developLocal, EnvironmentBranches.Develop, MappingMode.Deploy);
            deployGit.GetUnmergedBranches(Server, Database);
            var developPreview = deployGit.PreviewMerge(Server, Database, "PROJ-B");

            Assert.That(masterPreview.Leaks, Has.Count.EqualTo(1));
            Assert.That(masterPreview.Leaks[0].BranchName, Is.EqualTo("PROJ-A"));
            Assert.That(masterPreview.Leaks[0].Lines, Does.Contain(carried));
            Assert.That(developPreview.Leaks, Is.Empty, "develop 병합에서는 딸려 온 것이 원래 있던 곳으로 돌아갈 뿐입니다");
        }

        [Test]
        public void PreviewMerge_ReportsLeaks_WhenSourceAddsANewFile()
        {
            // 원본에서 파일이 새로 생기면 이전 블롭이 없다. 전체 줄이 추가로 세어져야 한다.
            var newPath = "dbo/Views/v_Discount.sql";
            var shared = "SELECT o.Id, o.DiscountRate FROM dbo.Orders o";
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Master, MappingMode.Audit);
            PushAuthorBranch(origin, "PROJ-A", EnvironmentBranches.Master, newPath, "CREATE OR ALTER VIEW dbo.v_Discount AS\n" + shared + "\n");
            PushAuthorBranch(origin, "PROJ-B", EnvironmentBranches.Master, newPath, "CREATE OR ALTER VIEW dbo.v_Discount AS\n" + shared + "\nWHERE 1 = 1\n");
            var git = NewPinnedGitManager(local, EnvironmentBranches.Master, MappingMode.Audit);
            git.GetUnmergedBranches(Server, Database);

            var preview = git.PreviewMerge(Server, Database, "PROJ-B");

            Assert.That(preview.Leaks.Single().Lines, Does.Contain(shared));
        }

        [Test]
        public void PreviewMerge_Throws_WhenSourceBranchDoesNotExist()
        {
            var (local, _) = NewPinnedClone(EnvironmentBranches.Develop);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var ex = Assert.Throws<InvalidOperationException>(() => git.PreviewMerge(Server, Database, "PROJ-404"));
            Assert.That(ex!.Message, Does.Contain("PROJ-404"));
        }

        // ---------- MergeAndPush ----------

        [Test]
        public void MergeAndPush_CreatesMergeCommit_WhenFastForwardIsPossible()
        {
            // develop이 원본의 조상이라 fast-forward가 가능해도 병합 커밋을 만든다(스펙 2.6).
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1", sourceSha);

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Merged), outcome.Message);
            Assert.That(outcome.Paths, Is.EqualTo(new[] { SqlPath }));
            using var remote = new Repository(origin);
            var tip = remote.Branches[EnvironmentBranches.Develop].Tip;
            Assert.That(tip.Parents.Count(), Is.EqualTo(2));
            Assert.That(tip.Parents.Select(p => p.Sha), Does.Contain(sourceSha));
            Assert.That(tip.MessageShort, Is.EqualTo("PROJ-1 브랜치를 develop에 병합"));
            Assert.That(tip.Author.Name, Is.EqualTo("Deployer"));
        }

        [Test]
        public void MergeAndPush_CatchesUp_WhenLocalIsBehindRemote()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            // 다른 사람이 먼저 develop에 병합해 둔 상황.
            PushAuthorBranch(origin, EnvironmentBranches.Develop, EnvironmentBranches.Develop, "dbo/Views/v_Other.sql", "other\n");
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1", sourceSha);

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Merged), outcome.Message);
            using var remote = new Repository(origin);
            Assert.That(remote.Branches[EnvironmentBranches.Develop].Tip.Tree["dbo/Views/v_Other.sql"], Is.Not.Null);
        }

        [Test]
        public void MergeAndPush_RestoresHead_WhenPushFails()
        {
            // 올라가지 않은 병합 커밋이 남으면 다음 병합이 LocalAhead에 걸리고, Push가 금지인 클론은
            // 도구 안에서 빠져나올 길이 없다(스펙 2.5).
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);
            string headBefore;
            using (var repo = new Repository(local)) headBefore = repo.Head.Tip.Sha;
            // 원격의 develop ref를 잠가 갱신을 실패시킨다. 파일 전송으로는 서버 거부를 재현할 수 없다.
            File.WriteAllText(Path.Combine(origin, "refs", "heads", "develop.lock"), string.Empty);

            // 실패는 예외(통신)로도 PushRejected(거부)로도 올 수 있다. 검증하는 것은 어느 쪽이든
            // 로컬이 되돌려졌다는 것이다.
            MergeOutcome? outcome = null;
            try { outcome = git.MergeAndPush(Server, Database, "PROJ-1", sourceSha); }
            catch (LibGit2SharpException) { }
            catch (GitRemoteException) { }

            Assert.That(outcome == null || outcome.Kind == MergeOutcomeKind.PushRejected, Is.True,
                "Push가 실패했는데 Merged로 보고하면 안 됩니다: " + outcome?.Kind);
            using var after = new Repository(local);
            Assert.That(after.Head.Tip.Sha, Is.EqualTo(headBefore));
            Assert.That(after.RetrieveStatus().IsDirty, Is.False);
            Assert.That(after.Info.CurrentOperation, Is.EqualTo(CurrentOperation.None));

            // 병합 커밋이 만들어지기 전에 실패해도 위 단언은 모두 통과한다. 되돌린 것이 실제로
            // 병합 커밋이었음을 reflog로 확인해야 이 테스트가 Push 실패 경로를 지킨다.
            var undone = after.Refs.Log("HEAD")
                .Select(e => after.Lookup<Commit>(e.To))
                .Where(c => c != null && c.Sha != headBefore)
                .ToList();
            Assert.That(undone.Any(c => c.MessageShort == "PROJ-1 브랜치를 develop에 병합" && c.Parents.Count() == 2), Is.True,
                "병합 커밋이 만들어진 뒤 되돌려져야 합니다");
            using var remote = new Repository(origin);
            Assert.That(remote.Branches[EnvironmentBranches.Develop].Tip.Sha, Is.EqualTo(headBefore));
        }

        [Test]
        public void MergeAndPush_RestoresHead_WhenMergeCheckoutFails()
        {
            // Push보다 앞에서 실패해도 MERGE_HEAD나 반쯤 쓴 파일이 남으면 다음 병합이 Refused(dirty)에
            // 걸리고, 버리기·Push가 금지인 클론은 막다른 길이 된다(스펙 2.5).
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            // 새 파일이 잠긴 파일보다 경로 순서가 앞서 체크아웃이 그것을 먼저 쓰고 실패한다.
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, "dbo/Functions/fn_New.sql", "new\n");
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", "PROJ-1", SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);
            string headBefore;
            using (var repo = new Repository(local)) headBefore = repo.Head.Tip.Sha;

            Exception? thrown;
            var lockedPath = Path.Combine(local, "dbo", "StoredProcedures", "usp_Order.sql");
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                thrown = Assert.Catch(() => git.MergeAndPush(Server, Database, "PROJ-1", sourceSha));
            }

            Assert.That(thrown, Is.Not.Null);
            using var after = new Repository(local);
            Assert.That(after.Head.Tip.Sha, Is.EqualTo(headBefore));
            Assert.That(after.Info.CurrentOperation, Is.EqualTo(CurrentOperation.None));
            Assert.That(after.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true }).IsDirty, Is.False,
                "체크아웃이 먼저 쓴 새 파일도 남으면 안 됩니다");
            using var remote = new Repository(origin);
            Assert.That(remote.Branches[EnvironmentBranches.Develop].Tip.Sha, Is.EqualTo(headBefore));
        }

        [Test]
        public void MergeAndPush_Refuses_WhenTrackedRemoteBranchIsDeleted()
        {
            // 원격 추적 ref가 없는데 검사를 건너뛰면, Push가 지워진 환경 브랜치를 이 클론의 이력으로 되살린다.
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);
            using (var repo = new Repository(origin)) repo.Branches.Remove(EnvironmentBranches.Develop);

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1", sourceSha);

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Refused), outcome.Message);
            Assert.That(outcome.Message, Does.Contain(EnvironmentBranches.Develop));
            using var remote = new Repository(origin);
            Assert.That(remote.Branches[EnvironmentBranches.Develop], Is.Null, "원격 develop이 되살아나면 안 됩니다");
        }

        [Test]
        public void MergeAndPush_ReturnsLocalAhead_WhenTrackedBranchIsNotTheTarget()
        {
            // Push는 HEAD가 추적하는 브랜치에 올린다. 앞섬 검사가 다른 ref를 보면 섞여 나갈 커밋을 놓친다.
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            string releaseBefore;
            using (var repo = new Repository(origin))
            {
                releaseBefore = repo.Branches[EnvironmentBranches.Master].Tip.Sha;
                repo.CreateBranch("release", releaseBefore);
            }
            using (var repo = new Repository(local))
            {
                WriteFile(local, "dbo/Views/v_Stray.sql", "stray\n");
                Commands.Stage(repo, "*");
                repo.Commit("밖에서 만든 커밋", Sig(), Sig());
                repo.Network.Push(repo.Network.Remotes["origin"], "refs/heads/develop:refs/heads/develop");
                Commands.Fetch(repo, "origin", Array.Empty<string>(), new FetchOptions(), null);
                repo.Branches.Update(repo.Head, b => b.UpstreamBranch = "refs/heads/release");
            }
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1", sourceSha);

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.LocalAhead), outcome.Message);
            using var remote = new Repository(origin);
            Assert.That(remote.Branches["release"].Tip.Sha, Is.EqualTo(releaseBefore));
        }

        [Test]
        public void MergeAndPush_ReturnsLocalAhead_WhenLocalHasUnpushedCommits()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            WriteFile(local, "dbo/Views/v_Stray.sql", "stray\n");
            using (var repo = new Repository(local))
            {
                Commands.Stage(repo, "*");
                repo.Commit("밖에서 만든 커밋", Sig(), Sig());
            }
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1", sourceSha);

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.LocalAhead));
            using var remote = new Repository(origin);
            Assert.That(remote.Branches[EnvironmentBranches.Develop].Tip.Parents.Count(), Is.EqualTo(0),
                "원격 develop은 첫 커밋 그대로여야 합니다");
        }

        [Test]
        public void MergeAndPush_Refuses_WhenTreeIsDirty()
        {
            // 7단계의 hard reset이 안전한 근거가 이 검사다.
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            WriteFile(local, SqlPath, "손으로 고친 내용\n");
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1", sourceSha);

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Refused));
            Assert.That(outcome.Paths, Is.EqualTo(new[] { SqlPath }));
            Assert.That(File.ReadAllText(Path.Combine(local, "dbo", "StoredProcedures", "usp_Order.sql")), Is.EqualTo("손으로 고친 내용\n"));
        }

        [TestCase("develop")]
        [TestCase("master")]
        public void MergeAndPush_Refuses_WhenSourceIsEnvironmentBranch(string source)
        {
            var (local, _) = NewPinnedClone(EnvironmentBranches.Master, MappingMode.Audit);
            var git = NewPinnedGitManager(local, EnvironmentBranches.Master, MappingMode.Audit);

            var outcome = git.MergeAndPush(Server, Database, source, "0000000000000000000000000000000000000000");

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Refused));
            Assert.That(outcome.Message, Does.Contain(source));
        }

        [Test]
        public void MergeAndPush_ReturnsAlreadyMerged_WhenSourceTipIsInTarget()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            string sourceSha;
            using (var repo = new Repository(origin)) sourceSha = repo.CreateBranch("PROJ-0", repo.Branches[EnvironmentBranches.Develop].Tip).Tip.Sha;
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);

            Assert.That(git.MergeAndPush(Server, Database, "PROJ-0", sourceSha).Kind, Is.EqualTo(MergeOutcomeKind.AlreadyMerged));
        }

        [Test]
        public void MergeAndPush_Refuses_WhenSourceMovedAfterPreview()
        {
            // 미리보기 뒤 올라온 커밋은 DBA가 바뀌는 파일도 경고 A도 보지 못한 것이다.
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);
            var preview = git.PreviewMerge(Server, Database, "PROJ-1");
            PushAuthorBranch(origin, "PROJ-1", "PROJ-1", "dbo/Views/v_Late.sql", "late\n");
            string developBefore;
            using (var repo = new Repository(origin)) developBefore = repo.Branches[EnvironmentBranches.Develop].Tip.Sha;

            var outcome = git.MergeAndPush(Server, Database, "PROJ-1", preview.SourceSha);

            Assert.That(outcome.Kind, Is.EqualTo(MergeOutcomeKind.Refused), outcome.Message);
            Assert.That(outcome.Message, Does.Contain("PROJ-1").And.Contain("미리보기"));
            using var remote = new Repository(origin);
            Assert.That(remote.Branches[EnvironmentBranches.Develop].Tip.Sha, Is.EqualTo(developBefore));
            using var after = new Repository(local);
            Assert.That(after.RetrieveStatus().IsDirty, Is.False);
        }

        [Test]
        public void PreviewMerge_ReturnsSourceSha_ForEveryResult()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var changed = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var conflicting = PushAuthorBranch(origin, "PROJ-B", EnvironmentBranches.Master, SqlPath, Proc("SELECT 'B'"));
            string merged;
            using (var repo = new Repository(origin))
            {
                repo.Refs.UpdateTarget("refs/heads/develop", changed);
                merged = repo.CreateBranch("PROJ-0", repo.Branches[EnvironmentBranches.Master].Tip).Tip.Sha;
            }
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Deploy);
            git.GetUnmergedBranches(Server, Database);
            var third = PushAuthorBranch(origin, "PROJ-2", EnvironmentBranches.Master, "dbo/Views/v_A.sql", "a\n");
            git.GetUnmergedBranches(Server, Database);

            Assert.That(git.PreviewMerge(Server, Database, "PROJ-B").SourceSha, Is.EqualTo(conflicting), "충돌");
            Assert.That(git.PreviewMerge(Server, Database, "PROJ-0").SourceSha, Is.EqualTo(merged), "이미 병합됨");
            Assert.That(git.PreviewMerge(Server, Database, "PROJ-2").SourceSha, Is.EqualTo(third), "병합 가능");
        }

        [Test]
        public void MergeAndPush_Throws_InWriteMode()
        {
            var (local, origin) = NewPinnedClone(EnvironmentBranches.Develop);
            var sourceSha = PushAuthorBranch(origin, "PROJ-1", EnvironmentBranches.Master, SqlPath, Proc("SELECT 2"));
            var git = NewPinnedGitManager(local, EnvironmentBranches.Develop, MappingMode.Write);

            Assert.Throws<OperationNotAllowedException>(() => git.MergeAndPush(Server, Database, "PROJ-1", sourceSha));
        }
    }
}
