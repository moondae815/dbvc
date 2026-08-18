using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 작업을 스레드 풀에서 실행하고 결과 처리만 VS/SSMS의 UI 스레드로 되돌린다.
    ///
    /// <see cref="InlineBackgroundScheduler"/>와 순서·예외 처리가 정확히 같도록 맞춰 두었다.
    /// 그래야 인라인 구현을 단위 테스트의 대역으로 쓰는 것이 정당해진다.
    /// </summary>
    public sealed class VsBackgroundScheduler : IBackgroundScheduler
    {
        public void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed)
        {
            // JoinableTaskFactory는 셸이 올라온 뒤에만 쓸 수 있으므로 생성자가 아니라 여기서 얻는다.
            var factory = ThreadHelper.JoinableTaskFactory;

            _ = factory.RunAsync(async () =>
            {
                T value;
                try
                {
                    // ConfigureAwait(false)로 UI 스레드 복귀를 미룬다.
                    // 복귀 지점은 아래 SwitchToMainThreadAsync 하나뿐이어야 한다.
                    value = await Task.Run(work).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await factory.SwitchToMainThreadAsync();
                    onFailed(ex);
                    return;
                }

                await factory.SwitchToMainThreadAsync();

                // onSucceeded의 예외는 잡지 않는다. 잡으면 UI 갱신 중의 결함이
                // 백그라운드 작업의 실패로 둔갑해 엉뚱한 곳을 보게 된다.
                onSucceeded(value);
            });
        }

        public void Post(Action action)
        {
            var factory = ThreadHelper.JoinableTaskFactory;

            _ = factory.RunAsync(async () =>
            {
                await factory.SwitchToMainThreadAsync();
                action();
            });
        }
    }
}
