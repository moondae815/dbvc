using System;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 무거운 작업을 UI 스레드 밖에서 실행하기 위한 이음매.
    ///
    /// DBVC의 새로고침은 SMO로 DB의 전 객체를 추출하고 libgit2로 작업 트리 상태를 읽는다.
    /// 커밋은 객체 3000개 기준 스테이징에만 15초가 걸린다. 이것을 명령이 실행되는 스레드에서
    /// 그대로 하면 그동안 SSMS 전체가 멈춘다 — 도구 창뿐 아니라 메뉴·쿼리 편집기·개체 탐색기까지.
    /// </summary>
    public interface IBackgroundScheduler
    {
        /// <summary>
        /// <paramref name="work"/>를 UI 스레드 밖에서 실행한다. 끝나면 그 결과로
        /// <paramref name="onSucceeded"/>를, 예외가 나면 <paramref name="onFailed"/>를 UI 스레드에서 실행한다.
        ///
        /// <paramref name="work"/>는 UI에 닿는 것을 건드리면 안 된다.
        /// ObservableCollection이나 바인딩 속성은 <paramref name="onSucceeded"/>에서만 만진다.
        /// </summary>
        void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed);
    }

    /// <summary>
    /// 넘겨받은 작업을 그 자리에서 그대로 실행한다.
    ///
    /// 단위 테스트와 셸 밖 실행을 위한 것이다. 실제 SSMS에서는 이것을 쓰면 안 된다 —
    /// 쓰면 이 이음매가 존재하는 이유가 사라진다. 도구 창의 배선은
    /// <see cref="DbvcServices.BackgroundScheduler"/>가 정한다.
    /// </summary>
    public sealed class InlineBackgroundScheduler : IBackgroundScheduler
    {
        public void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed)
        {
            T value;
            try
            {
                value = work();
            }
            catch (Exception ex)
            {
                onFailed(ex);
                return;
            }

            onSucceeded(value);
        }
    }
}
