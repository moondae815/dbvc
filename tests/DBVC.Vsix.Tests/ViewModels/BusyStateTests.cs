using System;
using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// 도구 줄의 진행 표시와 취소 버튼은 하나뿐이다. 배포 화면이 따로 들면
    /// 두 개가 각자 켜지고 꺼져 사용자가 무엇이 도는지 알 수 없게 된다.
    /// </summary>
    [TestFixture]
    public class BusyStateTests
    {
        [Test]
        public void IsNotBusy_MirrorsIsBusy()
        {
            var busy = new BusyState();

            Assert.That(busy.IsNotBusy, Is.True);

            busy.IsBusy = true;

            Assert.That(busy.IsNotBusy, Is.False);
        }

        [Test]
        public void Changed_Raises_WhenAnyValueChanges()
        {
            var busy = new BusyState();
            var count = 0;
            busy.Changed += (s, e) => count++;

            busy.IsBusy = true;
            busy.ProgressText = "추출하는 중...";
            busy.IsCancellable = true;

            Assert.That(count, Is.EqualTo(3));
        }

        [Test]
        public void Changed_DoesNotRaise_WhenValueIsUnchanged()
        {
            // 같은 값을 다시 넣을 때마다 CanExecute를 다시 계산하면 목록이 깜빡인다.
            var busy = new BusyState { IsBusy = true };
            var count = 0;
            busy.Changed += (s, e) => count++;

            busy.IsBusy = true;

            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void PropertyChanged_ReportsIsNotBusy_WhenIsBusyChanges()
        {
            // IsNotBusy는 계산 속성이라 스스로 알리지 못한다. 체크박스가 이것에 묶여 있다.
            var busy = new BusyState();
            var names = new List<string?>();
            busy.PropertyChanged += (s, e) => names.Add(e.PropertyName);

            busy.IsBusy = true;

            Assert.That(names, Does.Contain(nameof(BusyState.IsBusy)));
            Assert.That(names, Does.Contain(nameof(BusyState.IsNotBusy)));
        }
    }
}
