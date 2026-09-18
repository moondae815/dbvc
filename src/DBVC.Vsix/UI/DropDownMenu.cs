using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 버튼에 붙은 ContextMenu를 버튼 아래 드롭다운으로 연다. 도구 줄의 세 메뉴가 같은 함정을
    /// 공유하므로 한 곳에 둔다 - 하나만 고치고 나머지를 놓치면 그 메뉴만 조용히 먹통이 된다.
    /// </summary>
    public static class DropDownMenu
    {
        /// <summary>
        /// 메뉴 껍데기를 바꾸는 스타일의 리소스 키. 스타일을 XAML의 각 ContextMenu에 직접
        /// 붙이지 않고 여는 쪽에서 붙인다 - 네 번째 메뉴를 더하는 사람이 빠뜨리면 그 메뉴만
        /// 기본 템플릿의 흰 띠를 달고 뜬다.
        /// </summary>
        public const string MenuStyleKey = "DbvcDropDownMenuStyle";

        public static void Open(Button button)
        {
            var menu = Prepare(button);
            if (menu != null) menu.IsOpen = true;
        }

        /// <summary>
        /// 여는 것만 빼고 준비한다. 팝업을 띄우지 않고 배선을 검증하려는 테스트가 이것을 부른다.
        /// </summary>
        public static ContextMenu? Prepare(Button button)
        {
            var menu = button.ContextMenu;
            if (menu == null) return null;

            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;

            // 기본 템플릿이 고정색으로 그리는 아이콘 띠를 걷어낸다(스타일 주석 참고).
            if (button.TryFindResource(MenuStyleKey) is Style style)
            {
                menu.Style = style;
            }

            // ContextMenu는 별도 시각 트리라 DataContext를 상속받지 않는다. 주지 않으면 항목의
            // Command 바인딩이 조용히 실패해 메뉴는 뜨는데 눌러도 아무 일이 없다.
            menu.DataContext = button.DataContext;

            // DynamicResource로 두지 않는다 - 열리기 전의 ContextMenu는 컨트롤의 리소스 트리에 붙어
            // 있지 않아 키가 풀리는 시점을 보장할 수 없다. 열 때마다 버튼에서 다시 찾으므로 테마를
            // 바꿔도 다음에 열 때 따라간다. VS 밖(테스트·디자이너)에서 키가 없으면 WPF 기본값으로 둔다.
            if (button.TryFindResource(VsBrushes.ToolWindowBackgroundKey) is Brush background)
            {
                menu.Background = background;
            }

            if (button.TryFindResource(VsBrushes.ToolWindowTextKey) is Brush foreground)
            {
                menu.Foreground = foreground;
            }

            // 껍데기를 직접 그리게 되었으므로 테두리도 우리가 준다 - 주지 않으면 배경과 같은
            // 색의 면이 떠 있어 어디까지가 메뉴인지 보이지 않는다.
            if (button.TryFindResource(VsBrushes.CommandBarMenuBorderKey) is Brush border)
            {
                menu.BorderBrush = border;
            }

            return menu;
        }
    }
}
