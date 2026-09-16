using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// diff 추출 → 프롬프트 조립 → 호출 → 정제를 잇는다. 판단은 각 조각이 하고
    /// 여기는 순서만 정한다.
    /// </summary>
    public class CommitMessageGenerator : IAiCommitMessageGenerator
    {
        private readonly IGitManager _gitManager;
        private readonly IAiSettingsStore _settingsStore;
        private readonly IChatCompletionClient _client;

        public CommitMessageGenerator(
            IGitManager gitManager, IAiSettingsStore settingsStore, IChatCompletionClient client)
        {
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public string Generate(
            string serverName, string databaseName, IEnumerable<string> relativePaths, CancellationToken cancellationToken)
        {
            var settings = _settingsStore.Load();
            if (!settings.IsConfigured)
            {
                throw new AiRequestException(
                    "AI 설정이 없습니다. 도구 > 옵션 > DBVC > AI 커밋 메시지에서 프로바이더 주소와 모델을 설정하세요.");
            }

            var changes = _gitManager.GetUnifiedDiff(serverName, databaseName, relativePaths ?? Enumerable.Empty<string>());
            if (changes == null || changes.Count == 0)
            {
                // 변경이 없는데 부르면 모델이 문장을 지어낸다.
                throw new AiRequestException("선택한 항목에서 읽어 낼 변경 내용이 없습니다.");
            }

            var userMessage = CommitMessageComposer.BuildUserMessage(changes, settings.MaxDiffLines);

            // 이 메서드는 백그라운드 스레드에서만 불려야 한다(IBackgroundScheduler로 예약된
            // 작업). GetAwaiter().GetResult()로 동기 대기해도 UI 스레드가 아니므로 막히지
            // 않는다 — UI 스레드에서 부르면 그 스레드를 통째로 잠가 SSMS 전체가 멈춘다.
            var raw = _client
                .CompleteAsync(settings, CommitMessageComposer.SystemPrompt, userMessage, cancellationToken)
                .GetAwaiter()
                .GetResult();

            var message = CommitMessageComposer.Clean(raw);
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new AiRequestException("AI가 빈 응답을 돌려주었습니다. 다시 시도하거나 직접 입력하세요.");
            }

            return message;
        }
    }
}
