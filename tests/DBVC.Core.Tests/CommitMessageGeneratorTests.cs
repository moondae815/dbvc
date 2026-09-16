using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class CommitMessageGeneratorTests
    {
        private sealed class StubSettingsStore : IAiSettingsStore
        {
            public AiSettings Settings { get; set; } = new AiSettings
            {
                BaseUrl = "https://llm.example.com/v1",
                Model = "m",
                MaxDiffLines = 400,
            };

            public AiSettings Load() => Settings;
            public void Save(AiSettings settings) => Settings = settings;
        }

        private sealed class StubClient : IChatCompletionClient
        {
            public string Response { get; set; } = "feat: 주문 뷰를 더한다";
            public string? LastUserMessage { get; private set; }
            public int CallCount { get; private set; }

            public Task<string> CompleteAsync(
                AiSettings settings, string systemPrompt, string userMessage, CancellationToken cancellationToken)
            {
                CallCount++;
                LastUserMessage = userMessage;
                return Task.FromResult(Response);
            }
        }

        /// <summary>GetUnifiedDiff만 답하는 최소 대역. 나머지는 이 테스트가 부르지 않는다.</summary>
        private sealed class StubGitManager : FakeGitManagerBase
        {
            public List<DiffFileChange> Changes { get; } = new List<DiffFileChange>();

            public override IReadOnlyList<DiffFileChange> GetUnifiedDiff(
                string serverName, string databaseName, IEnumerable<string> relativePaths) => Changes;
        }

        private static CommitMessageGenerator Build(StubGitManager git, StubSettingsStore store, StubClient client) =>
            new CommitMessageGenerator(git, store, client);

        [Test]
        public void Generate_ReturnsCleanedMessage_WhenDiffPresent()
        {
            var git = new StubGitManager();
            git.Changes.Add(new DiffFileChange
            {
                RelativePath = "dbo/Views/v_Order.sql", Status = "Added", Patch = "@@ -0 +1 @@\n+CREATE VIEW",
            });
            var client = new StubClient { Response = "```\nfeat: 주문 뷰를 더한다\n```" };

            var message = Build(git, new StubSettingsStore(), client)
                .Generate("localhost", "testdb", new[] { "dbo/Views/v_Order.sql" }, CancellationToken.None);

            Assert.That(message, Is.EqualTo("feat: 주문 뷰를 더한다"));
        }

        [Test]
        public void Generate_SendsObjectNameInPrompt_WhenDiffPresent()
        {
            var git = new StubGitManager();
            git.Changes.Add(new DiffFileChange
            {
                RelativePath = "dbo/Views/v_Order.sql", Status = "Added", Patch = "@@ -0 +1 @@\n+CREATE VIEW",
            });
            var client = new StubClient();

            Build(git, new StubSettingsStore(), client)
                .Generate("localhost", "testdb", new[] { "dbo/Views/v_Order.sql" }, CancellationToken.None);

            Assert.That(client.LastUserMessage, Does.Contain("dbo.v_Order"));
        }

        [Test]
        public void Generate_Throws_WhenSettingsIncomplete()
        {
            // 주소·모델을 명시적으로 비운다. AiSettings에 기본값이 생긴 뒤로
            // new AiSettings()는 "설정이 없는 상태"가 아니다 — 이 갈래가 살아 있는지 보려면
            // 사용자가 일부러 비운 상태를 만들어야 한다.
            var store = new StubSettingsStore
            {
                Settings = new AiSettings { BaseUrl = string.Empty, Model = string.Empty },
            };
            var client = new StubClient();

            var ex = Assert.Throws<AiRequestException>(() =>
                Build(new StubGitManager(), store, client)
                    .Generate("localhost", "testdb", new[] { "a.sql" }, CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("옵션"));
                Assert.That(client.CallCount, Is.Zero);
            });
        }

        [Test]
        public void Generate_Throws_WhenNoDiffFound()
        {
            // 변경이 없는데 호출하면 모델이 문장을 지어낸다. 부르기 전에 멈춘다.
            var client = new StubClient();

            var ex = Assert.Throws<AiRequestException>(() =>
                Build(new StubGitManager(), new StubSettingsStore(), client)
                    .Generate("localhost", "testdb", new[] { "a.sql" }, CancellationToken.None));

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("변경"));
                Assert.That(client.CallCount, Is.Zero);
            });
        }

        [Test]
        public void Generate_Throws_WhenModelReturnsBlank()
        {
            var git = new StubGitManager();
            git.Changes.Add(new DiffFileChange { RelativePath = "dbo/Views/v.sql", Status = "Added", Patch = "+x" });
            var client = new StubClient { Response = "   " };

            var ex = Assert.Throws<AiRequestException>(() =>
                Build(git, new StubSettingsStore(), client)
                    .Generate("localhost", "testdb", new[] { "dbo/Views/v.sql" }, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("빈"));
        }
    }
}
