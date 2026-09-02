using System;
using System.Collections.Generic;
using System.Linq;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// ViewChangesViewModelTests와 DeploymentViewModelTests가 함께 쓰는 테스트 더블.
    /// 갈라 두면 한쪽만 고쳐진 채로 남는다.
    /// </summary>
    internal sealed class RecordingSaveDialog : IFileSaveDialog
    {
        public string? PathToReturn { get; set; }
        public int CallCount { get; private set; }
        public string? LastDefaultFileName { get; private set; }

        /// <summary>대화상자가 실제로 열렸는지. 제외만으로 스크립트가 비면 열리지 않아야 한다.</summary>
        public bool WasPrompted { get; private set; }

        public string? PromptForSavePath(string title, string defaultFileName)
        {
            CallCount++;
            WasPrompted = true;
            LastDefaultFileName = defaultFileName;
            return PathToReturn;
        }
    }

    internal sealed class RecordingNotifier : IUserNotifier
    {
        public List<string> Errors { get; } = new List<string>();
        public List<string> Infos { get; } = new List<string>();

        /// <summary>ShowInfo에 실제로 전달된 (title, message) 쌍.</summary>
        public List<(string Title, string Message)> InfoCalls { get; } = new List<(string, string)>();

        /// <summary>
        /// ShowError에 실제로 전달된 (title, message) 쌍.
        /// Errors는 message만 담아 기존 테스트를 그대로 두는데, 그것만으로는
        /// title이 다른 두 catch 분기(예: 병합 충돌 vs. 예기치 못한 실패)를
        /// 구분해서 검증할 수 없다.
        /// </summary>
        public List<(string Title, string Message)> ErrorCalls { get; } = new List<(string, string)>();

        /// <summary>Confirm의 응답. 기본이 "계속"이라 기존 테스트의 동작이 바뀌지 않는다.</summary>
        public bool ConfirmResult { get; set; } = true;
        public int ConfirmCallCount { get; private set; }

        /// <summary>Confirm에 실제로 전달된 (title, message) 쌍. 문구 자체를 검증할 때 쓴다.</summary>
        public List<(string Title, string Message)> ConfirmCalls { get; } = new List<(string, string)>();

        public void ShowError(string title, string message)
        {
            Errors.Add(message);
            ErrorCalls.Add((title, message));
        }

        public void ShowInfo(string title, string message)
        {
            Infos.Add(message);
            InfoCalls.Add((title, message));
        }

        public bool Confirm(string title, string message)
        {
            ConfirmCallCount++;
            ConfirmCalls.Add((title, message));
            return ConfirmResult;
        }
    }

    internal sealed class RecordingConnectDialog : IRepositoryConnectDialog
    {
        public RepositoryConnectRequest? RequestToReturn { get; set; }
        public int CallCount { get; private set; }

        public RepositoryConnectRequest? Prompt(string serverName, string databaseName)
        {
            CallCount++;
            return RequestToReturn;
        }
    }

    /// <summary>
    /// 작업은 즉시 돌리되 결과 반영은 큐에 담아 둔다. 테스트가 순서를 골라 흘려보내
    /// "늦게 끝난 앞선 요청"을 흉내 낼 수 있다 - stale 가드는 그 상황에서만 드러난다.
    /// </summary>
    public sealed class DeferredBackgroundScheduler : IBackgroundScheduler
    {
        private readonly List<Action> _pending = new List<Action>();

        public int PendingCount => _pending.Count;

        public void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed)
        {
            T value;
            try
            {
                value = work();
            }
            catch (Exception ex)
            {
                _pending.Add(() => onFailed(ex));
                return;
            }

            _pending.Add(() => onSucceeded(value));
        }

        public void Post(Action action) => action();

        /// <summary>담긴 순서대로 모두 흘린다.</summary>
        public void FlushAll()
        {
            var snapshot = _pending.ToList();
            _pending.Clear();
            foreach (var callback in snapshot) callback();
        }

        /// <summary><paramref name="index"/>번째 콜백만 흘린다.</summary>
        public void FlushAt(int index)
        {
            var callback = _pending[index];
            _pending.RemoveAt(index);
            callback();
        }
    }
}
