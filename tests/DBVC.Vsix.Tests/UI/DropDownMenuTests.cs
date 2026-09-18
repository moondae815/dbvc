#if NETFRAMEWORK
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NUnit.Framework;
using DBVC.Core.Models;
using DBVC.Vsix.UI;
using DBVC.Vsix.ViewModels;
using static DBVC.Vsix.Tests.UI.ViewChangesControlFixtures;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 도구 줄 드롭다운 메뉴의 배선. 셋 다 "메뉴는 뜨는데 눌러도 아무 일이 없다"로 조용히 깨지는
    /// 종류라, 팝업을 띄우지 않고 준비 단계(DropDownMenu.Prepare)의 결과를 직접 본다.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class DropDownMenuTests
    {
        /// <summary>
        /// ContextMenu는 별도 시각 트리라 DataContext를 상속받지 않는다. Prepare가 넘겨주지 않으면
        /// 항목의 Command 바인딩이 null로 남는다.
        /// </summary>
        [TestCase("BranchMenuButton", 0, nameof(ViewChangesViewModel.SwitchBranchCommand))]
        [TestCase("BranchMenuButton", 1, nameof(ViewChangesViewModel.CreateBranchCommand))]
        [TestCase("RefreshMenuButton", 0, nameof(ViewChangesViewModel.RefreshAllCommand))]
        [TestCase("ScriptMenuButton", 0, nameof(ViewChangesViewModel.GenerateDeploymentScriptCommand))]
        [TestCase("ScriptMenuButton", 1, nameof(ViewChangesViewModel.GenerateRollbackScriptCommand))]
        public void Prepare_BindsTheMenuItemToTheViewModelCommand(string buttonName, int itemIndex, string commandName)
        {
            var control = NewControl();
            LayoutAt(control, 600);
            var vm = (ViewChangesViewModel)control.DataContext;

            var menu = DropDownMenu.Prepare(Find<Button>(control, buttonName));
            // 바인딩은 DataContext 변경 뒤 DataBind 우선순위에서 갱신될 수 있다. 그때까지 흘려보낸다.
            control.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            Assert.That(menu, Is.Not.Null, "버튼에 ContextMenu가 붙어 있어야 합니다");
            Assert.That(menu!.DataContext, Is.SameAs(vm));
            var item = (MenuItem)menu.Items[itemIndex];
            var expected = typeof(ViewChangesViewModel).GetProperty(commandName)!.GetValue(vm);
            Assert.That(item.Command, Is.SameAs(expected));
        }

        /// <summary>
        /// 메뉴는 VS가 칠해 주지 않는다. WPF 기본 전경도 검정이라 기본값과 겹치지 않는 색(Magenta)을
        /// 주어 "셸 브러시를 받았는지"만 가른다 - AuthorToggle 테스트와 같은 방식이다.
        /// </summary>
        [TestCase("BranchMenuButton")]
        [TestCase("RefreshMenuButton")]
        [TestCase("ScriptMenuButton")]
        public void Prepare_TakesTheMenuColorsFromTheShellTheme(string buttonName)
        {
            var control = NewControl();
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowTextKey] = Brushes.Magenta;
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBackgroundKey] = Brushes.Cyan;
            LayoutAt(control, 600);

            var menu = DropDownMenu.Prepare(Find<Button>(control, buttonName))!;

            Assert.That(menu.Foreground, Is.SameAs(Brushes.Magenta));
            Assert.That(menu.Background, Is.SameAs(Brushes.Cyan));
        }

        /// <summary>
        /// 우클릭으로 열리면 Prepare를 거치지 않아 항목이 눌려도 아무 일이 없다. 우클릭 메뉴는
        /// 설계상 쓰지 않으므로(스펙 2.3) 자동 열림을 끈다.
        /// </summary>
        [TestCase("BranchMenuButton")]
        [TestCase("RefreshMenuButton")]
        [TestCase("ScriptMenuButton")]
        public void DropDownButton_DoesNotOpenOnRightClick(string buttonName)
        {
            var control = NewControl();

            Assert.That(ContextMenuService.GetIsEnabled(Find<Button>(control, buttonName)), Is.False);
        }

        /// <summary>
        /// WPF 기본 ContextMenu 템플릿은 아이콘 자리로 28px 띠를 #F1F1F1로 박아 그린다 — 배경을
        /// 셸 색으로 칠해도 그 띠는 그대로라, 어두운 테마에서 글자 앞에 흰 여백으로 남는다
        /// (2026-09-18 SSMS 21에서 실제로 그렇게 보였다). 그래서 템플릿을 통째로 바꾼다.
        /// </summary>
        [TestCase("BranchMenuButton")]
        [TestCase("RefreshMenuButton")]
        [TestCase("ScriptMenuButton")]
        public void Prepare_ReplacesTheMenuTemplate_SoTheFixedColorIconGutterIsGone(string buttonName)
        {
            var control = NewControl();
            LayoutAt(control, 600);

            var menu = DropDownMenu.Prepare(Find<Button>(control, buttonName))!;

            var style = control.TryFindResource(DropDownMenu.MenuStyleKey) as Style;
            Assert.That(style, Is.Not.Null, $"'{DropDownMenu.MenuStyleKey}' 스타일이 컨트롤 리소스에 있어야 한다");
            Assert.That(menu.Style, Is.SameAs(style), "여는 쪽이 스타일을 붙여야 새 메뉴도 함께 고쳐진다");

            var content = menu.Template.LoadContent();
            Assert.That(content, Is.TypeOf<Border>(), "템플릿 뿌리는 배경을 물려받는 Border여야 한다");
            Assert.That(((Border)content).Child, Is.TypeOf<ItemsPresenter>(),
                "Border 안에 항목만 둔다 - 기본 템플릿이 그리던 아이콘 띠가 여기 없어야 한다");
        }

        /// <summary>
        /// 배포·감사 클론의 윗줄에도 브랜치 버튼은 보이고 메뉴도 열리지만, 두 항목은 잠겨 있어야 한다
        /// (스펙 2.7). 고정 브랜치를 옮기는 길이 메뉴로 새어 나오면, 병합 영역이 가리키는 목적지와
        /// 저장소가 어긋나 차단 오버레이가 뜨는 상태를 사용자가 스스로 만든다.
        /// </summary>
        [Test]
        public void BranchMenu_ItemsAreDisabled_WhenTheTargetIsADeployClone()
        {
            var control = NewConnectedControl(
                new RepositoryState { CurrentBranch = "develop", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Deploy);
            LayoutAt(control, 600);

            var button = Find<Button>(control, "BranchMenuButton");
            Assert.That(button.Visibility, Is.EqualTo(Visibility.Visible), "배포 클론에서도 브랜치 이름은 보여야 한다");

            var menu = DropDownMenu.Prepare(button)!;
            control.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            Assert.That(menu.Items.Count, Is.EqualTo(2));
            foreach (MenuItem item in menu.Items)
            {
                Assert.That(item.Command, Is.Not.Null, $"'{item.Header}'의 명령 바인딩이 풀리지 않았다");
                Assert.That(item.Command!.CanExecute(null), Is.False, $"'{item.Header}'는 배포 클론에서 잠겨야 한다");
            }
        }
    }
}
#endif
