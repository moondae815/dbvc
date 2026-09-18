#if NETFRAMEWORK
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.UI;
using static DBVC.Vsix.Tests.UI.ViewChangesControlFixtures;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 변경 목록 위 도구 줄(동기화·커밋·체크)의 배치. WPF 레이아웃은 CI가 검증하지 않는 영역이라
    /// 실제 컨트롤을 STA로 배치해 좌표와 값을 직접 본다.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class ChangeListToolbarLayoutTests
    {
        /// <summary>초기화된 Write 대상 - 변경 목록 영역이 보이는 상태다.</summary>
        private static ViewChangesControl NewWriteControl(RemoteStatus? remoteStatus = null)
            => NewConnectedControl(
                new RepositoryState { CurrentBranch = "develop", BlockReason = RepositoryBlockReason.None },
                remoteStatus,
                installedVersion: StateTracker.RequiredSchemaVersion);

        [Test]
        public void PullAndPushButtons_ShowTheCounts_AfterCheckingTheRemote()
        {
            var control = NewWriteControl(new RemoteStatus(2, 1));

            LayoutAt(control, 600);

            Assert.That(Find<Button>(control, "PullButton").Content, Is.EqualTo("Pull ↓1"));
            Assert.That(Find<Button>(control, "PushButton").Content, Is.EqualTo("Push ↑2"));
        }

        /// <summary>넓은 창에서는 추출 무리와 동기화 무리가 한 줄에 있어야 한다. 아니면 아래 좁은 창 테스트의 전제가 없다.</summary>
        [Test]
        public void SyncGroup_SharesTheLineWithRefresh_WhenTheWindowIsWide()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);

            Assert.That(TopLeftOf(control, "PullButton").Y,
                Is.EqualTo(TopLeftOf(control, "RefreshButton").Y).Within(1));
        }

        /// <summary>
        /// 좁은 창에서는 동기화 무리가 통째로 내려가야 한다. 버튼을 한 WrapPanel에 흘려 두면 Pull만
        /// 윗줄에 남고 Push가 아랫줄로 떨어져, 함께 읽어야 할 숫자가 갈라진다.
        /// </summary>
        [Test]
        public void SyncGroup_WrapsAsOneUnit_WhenTheWindowIsNarrow()
        {
            var control = NewWriteControl(new RemoteStatus(12, 34));

            LayoutAt(control, 300);

            var refresh = TopLeftOf(control, "RefreshButton").Y;
            var pull = TopLeftOf(control, "PullButton").Y;
            Assert.That(pull, Is.GreaterThan(refresh + 6),
                "300px에서는 동기화 무리가 다음 줄로 내려가야 한다. 아니면 이 테스트의 전제가 틀렸다.");
            Assert.That(TopLeftOf(control, "PushButton").Y, Is.EqualTo(pull).Within(1));
            Assert.That(TopLeftOf(control, "CheckRemoteButton").Y, Is.EqualTo(pull).Within(1));
        }

        /// <summary>메시지 칸은 남는 폭을 모두 써야 한다. 고정 240이면 넓은 창에서 칸만 좁게 남는다.</summary>
        [Test]
        public void CommitMessageBox_Widens_WhenTheWindowWidens()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);
            var narrow = Find<FrameworkElement>(control, "CommitMessageBox").ActualWidth;
            LayoutAt(control, 900);
            var wide = Find<FrameworkElement>(control, "CommitMessageBox").ActualWidth;

            Assert.That(wide, Is.GreaterThan(narrow + 200));
        }

        /// <summary>안내 글자는 칸이 비었을 때만 보여야 한다. 적은 글자 위에 겹치면 읽히지 않는다.</summary>
        [Test]
        public void CommitMessagePlaceholder_IsVisibleOnlyWhileTheMessageIsEmpty()
        {
            var control = NewWriteControl();
            var vm = (DBVC.Vsix.ViewModels.ViewChangesViewModel)control.DataContext;

            LayoutAt(control, 600);
            Assert.That(Find<FrameworkElement>(control, "CommitMessagePlaceholder").Visibility,
                Is.EqualTo(Visibility.Visible), "메시지가 비어 있으면 안내가 보여야 한다");

            vm.CommitMessage = "기능 추가";
            LayoutAt(control, 600);
            Assert.That(Find<FrameworkElement>(control, "CommitMessagePlaceholder").Visibility,
                Is.Not.EqualTo(Visibility.Visible), "메시지가 있으면 안내가 사라져야 한다");

            vm.CommitMessage = "";
            LayoutAt(control, 600);
            Assert.That(Find<FrameworkElement>(control, "CommitMessagePlaceholder").Visibility,
                Is.EqualTo(Visibility.Visible), "지우면 다시 보여야 한다");
        }

        [Test]
        public void CheckedCountLabel_ShowsTheCount()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);

            Assert.That(Find<TextBlock>(control, "CheckedCountLabel").Text, Is.EqualTo("체크한 항목 0개"));
        }

        /// <summary>
        /// 체크 작업 줄은 목록 바로 위에 있어야 한다 - 체크한 항목에 작용한다는 것을 자리가 말한다.
        /// 커밋 줄보다 아래, 목록보다 위.
        /// </summary>
        [Test]
        public void CheckedItemRow_SitsBetweenTheCommitRowAndTheList()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);

            var commit = TopLeftOf(control, "CommitButton").Y;
            var discard = TopLeftOf(control, "DiscardButton").Y;
            var list = TopLeftOf(control, "ChangeList").Y;
            Assert.That(discard, Is.GreaterThan(commit + 6));
            Assert.That(list, Is.GreaterThan(discard + 6));
        }

        /// <summary>
        /// 체크 줄의 버튼은 CanExecute가 잠근다. 화면에 IsEnabled를 따로 걸면 같은 판정이 두 곳에
        /// 생긴다(스펙 2.4) - 변경이 없으면 잠겨 있어야 하고, 그것을 명령이 해냈는지만 본다.
        /// </summary>
        [Test]
        public void DiscardAndIgnoreButtons_AreDisabled_WhenNothingIsChecked()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);

            Assert.That(Find<Button>(control, "DiscardButton").IsEnabled, Is.False);
            Assert.That(Find<Button>(control, "IgnoreButton").IsEnabled, Is.False);
        }

        /// <summary>
        /// 새 도구 줄은 변경 목록 영역(ChangeListGrid) 안에 있어야 한다. 윗줄로 끌어올리거나 바깥 Grid에
        /// 두면 배포·감사 클론에서 병합 영역 위에 Pull·Push·Commit이 새어 나온다 - 배포 클론은 Push가
        /// 금지이고, 병합은 자기가 만든 커밋 하나만 올리도록 짜여 있다(스펙 2.7).
        /// </summary>
        [TestCase("RefreshButton")]
        [TestCase("PullButton")]
        [TestCase("CommitButton")]
        [TestCase("DiscardButton")]
        [TestCase("ScriptMenuButton")]
        public void ChangeListToolbar_StaysInsideTheChangeList_WhenTheTargetIsADeployClone(string name)
        {
            var control = NewConnectedControl(
                new RepositoryState { CurrentBranch = "develop", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Deploy);

            LayoutAt(control, 600);

            var changeList = Find<FrameworkElement>(control, "ChangeListGrid");
            var deployment = Find<FrameworkElement>(control, "DeploymentPanelGrid");
            Assert.That(deployment.Visibility, Is.EqualTo(Visibility.Visible), "전제: 배포 클론은 배포 패널을 본다");
            Assert.That(changeList.Visibility, Is.Not.EqualTo(Visibility.Visible), "전제: 변경 목록 영역은 숨는다");
            Assert.That(IsDescendantOf(Find<DependencyObject>(control, name), changeList), Is.True,
                $"'{name}'이 변경 목록 영역 밖에 있어 배포 화면에 보인다");
        }

        /// <summary>
        /// 목록·머리글·입력 칸은 셸 테마 색을 받아야 한다. WPF 기본값(흰 바탕)으로 두면 어두운
        /// 테마에서 도구 창 한가운데에 흰 상자가 남는다(0.9.1 실기 확인). 기본 전경도 검정이라
        /// 기본값과 겹치지 않는 색을 주어 "받았는지"만 가른다 - AuthorToggle 테스트와 같은 방식이다.
        /// </summary>
        [Test]
        public void ListAndInput_TakeTheirColorsFromTheShellTheme()
        {
            var control = NewWriteControl();
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowTextKey] = Brushes.Magenta;
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBackgroundKey] = Brushes.Cyan;

            LayoutAt(control, 600);

            Assert.That(Find<ListView>(control, "ChangeList").Foreground, Is.SameAs(Brushes.Magenta),
                "변경 목록이 셸 글자색을 받아야 한다");
            Assert.That(Find<ListView>(control, "ChangeList").Background, Is.SameAs(Brushes.Cyan),
                "변경 목록이 셸 배경색을 받아야 한다");
            Assert.That(Find<TextBox>(control, "CommitMessageBox").Background, Is.SameAs(Brushes.Cyan),
                "커밋 메시지 칸이 셸 배경색을 받아야 한다");
        }

        /// <summary>
        /// 목록을 어둡게 칠하면 선택 행이 문제가 된다 - WPF 기본 선택 배경은 밝은 그라데이션이라
        /// 밝은 글자와 겹쳐 읽히지 않는다(탐침으로 확인). 선택 행도 셸 색을 받아야 한다.
        /// </summary>
        [Test]
        public void SelectedRow_TakesTheShellHighlightColors()
        {
            var control = NewWriteControl();
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.HighlightKey] = Brushes.DarkBlue;
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.HighlightTextKey] = Brushes.Yellow;
            var vm = (DBVC.Vsix.ViewModels.ViewChangesViewModel)control.DataContext;
            vm.Changes.Add(new DBVC.Vsix.ViewModels.ChangeItemViewModel
            {
                ObjectName = "dbo.Users",
                ObjectType = "Table",
                State = "Modified",
                RelativePath = "dbo/Tables/Users.sql",
                Author = "DEV-PC"
            });

            LayoutAt(control, 600);

            var list = Find<ListView>(control, "ChangeList");
            var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.That(row, Is.Not.Null, "행이 만들어져야 선택 색을 볼 수 있다");

            row.IsSelected = true;
            LayoutAt(control, 600);

            Assert.That(row.Background, Is.SameAs(Brushes.DarkBlue));
            Assert.That(row.Foreground, Is.SameAs(Brushes.Yellow));
        }

        private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
        {
            for (var current = node; current != null; current = LogicalTreeHelper.GetParent(current))
            {
                if (ReferenceEquals(current, ancestor)) return true;
            }

            return false;
        }
    }
}
#endif
