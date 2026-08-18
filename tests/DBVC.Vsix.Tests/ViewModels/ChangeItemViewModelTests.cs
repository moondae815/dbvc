using NUnit.Framework;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    [TestFixture]
    public class ChangeItemViewModelTests
    {
        [Test]
        public void Properties_CanBeSetAndRetrieved()
        {
            var item = new ChangeItemViewModel
            {
                IsSelected = true,
                ObjectName = "dbo.TestTable",
                State = "Modified"
            };

            Assert.That(item.IsSelected, Is.True);
            Assert.That(item.ObjectName, Is.EqualTo("dbo.TestTable"));
            Assert.That(item.State, Is.EqualTo("Modified"));
        }

        [Test]
        public void IsSelected_RaisesPropertyChanged()
        {
            var item = new ChangeItemViewModel();
            bool raised = false;
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ChangeItemViewModel.IsSelected))
                {
                    raised = true;
                }
            };

            item.IsSelected = true;

            Assert.That(raised, Is.True);
        }

        [TestCase("Added", "추가")]
        [TestCase("Modified", "수정")]
        [TestCase("Deleted", "삭제")]
        public void StateText_TranslatesTheCoreState(string state, string expected)
        {
            var item = new ChangeItemViewModel { State = state };

            Assert.That(item.StateText, Is.EqualTo(expected));
        }

        [Test]
        public void StateText_PassesThroughAnUnknownState()
        {
            // Core가 새 상태값을 내놓게 되면 조용히 빈칸이 되는 대신 원문이 보여야 한다.
            // 번역표에 없는 값이 생겼다는 사실 자체가 화면에 드러나야 알아챌 수 있다.
            var item = new ChangeItemViewModel { State = "Renamed" };

            Assert.That(item.StateText, Is.EqualTo("Renamed"));
        }

        [Test]
        public void StateText_IsEmpty_WhenStateIsNull()
        {
            var item = new ChangeItemViewModel { State = null };

            Assert.That(item.StateText, Is.EqualTo(string.Empty));
        }
    }
}
