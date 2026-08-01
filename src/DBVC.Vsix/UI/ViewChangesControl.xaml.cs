using System;
using System.Windows.Controls;
using System.Windows.Threading;
using DBVC.Vsix.Services;
using DBVC.Vsix.ViewModels;
using ICSharpCode.AvalonEdit;

namespace DBVC.Vsix.UI
{
    public partial class ViewChangesControl : UserControl
    {
        private readonly ViewChangesViewModel _viewModel;
        private readonly DiffService _diffService;
        private readonly DiffLineBackgroundRenderer _oldRenderer;
        private readonly DiffLineBackgroundRenderer _newRenderer;
        private bool _syncingScroll;

        public ViewChangesControl()
            : this(DbvcServices.Default.SharedViewChangesViewModel, DbvcServices.Default.CreateDiffService())
        {
        }

        public ViewChangesControl(ViewChangesViewModel viewModel, DiffService? diffService)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _diffService = diffService ?? new DiffService();

            InitializeComponent();
            DataContext = _viewModel;

            _oldRenderer = new DiffLineBackgroundRenderer(OldTextEditor.TextArea.TextView);
            _newRenderer = new DiffLineBackgroundRenderer(NewTextEditor.TextArea.TextView);
            OldTextEditor.TextArea.TextView.BackgroundRenderers.Add(_oldRenderer);
            NewTextEditor.TextArea.TextView.BackgroundRenderers.Add(_newRenderer);

            OldTextEditor.TextArea.TextView.ScrollOffsetChanged += OnOldScrollOffsetChanged;
            NewTextEditor.TextArea.TextView.ScrollOffsetChanged += OnNewScrollOffsetChanged;

            _viewModel.SelectionChanged += OnSelectionChanged;
            Unloaded += (_, __) =>
            {
                _viewModel.SelectionChanged -= OnSelectionChanged;
                OldTextEditor.TextArea.TextView.ScrollOffsetChanged -= OnOldScrollOffsetChanged;
                NewTextEditor.TextArea.TextView.ScrollOffsetChanged -= OnNewScrollOffsetChanged;
            };
        }

        /// <summary>
        /// 선택된 객체의 Git HEAD 버전과 현재 DB 버전을 좌우 에디터에 채우고 차이를 강조한다.
        /// </summary>
        private void OnSelectionChanged(object? sender, EventArgs e)
        {
            var selected = _viewModel.SelectedChange;
            if (selected == null || _viewModel.ServerName == null || _viewModel.DatabaseName == null)
            {
                // 빈 문서도 실제로는 1줄이라는 계약(DiffTextBuilder.Build(null))을
                // 여기서도 그대로 따른다. new DiffPane()의 빈 LineKinds도 렌더링 결과는 같지만
                // "빈 문서는 한 줄"이라는 계약을 한 곳에서만 정의해야 나중에 어긋나지 않는다.
                SetPane(OldTextEditor, _oldRenderer, DiffTextBuilder.Build(null));
                SetPane(NewTextEditor, _newRenderer, DiffTextBuilder.Build(null));
                return;
            }

            // Diff 생성 실패(신규 객체 등)는 빈 쪽으로 자연스럽게 표현되며 예외를 던지지 않는다.
            var model = _diffService.GetDiffModel(
                _viewModel.ServerName,
                _viewModel.DatabaseName,
                selected.RelativePath);

            SetPane(OldTextEditor, _oldRenderer, DiffTextBuilder.Build(model.OldText.Lines));
            SetPane(NewTextEditor, _newRenderer, DiffTextBuilder.Build(model.NewText.Lines));
        }

        /// <summary>텍스트를 먼저 넣고 줄 종류를 넘긴다. 순서가 반대면 이전 종류로 한 번 그린다.</summary>
        private static void SetPane(TextEditor editor, DiffLineBackgroundRenderer renderer, DiffPane pane)
        {
            editor.Text = pane.Text;
            renderer.SetLineKinds(pane.LineKinds);
        }

        private void OnOldScrollOffsetChanged(object? sender, EventArgs e) => SyncScroll(OldTextEditor, NewTextEditor);

        private void OnNewScrollOffsetChanged(object? sender, EventArgs e) => SyncScroll(NewTextEditor, OldTextEditor);

        /// <summary>좌우가 줄 단위로 정렬되어 있으므로 오프셋을 그대로 옮긴다.</summary>
        private void SyncScroll(TextEditor source, TextEditor target)
        {
            if (_syncingScroll) return;

            _syncingScroll = true;
            target.ScrollToVerticalOffset(source.VerticalOffset);
            target.ScrollToHorizontalOffset(source.HorizontalOffset);

            // ScrollTo*Offset은 ScrollViewer에 목표 오프셋을 예약하고 measure를 무효화할 뿐이다.
            // AvalonEdit TextView의 실제 IScrollInfo 반영과 그로 인한 ScrollOffsetChanged 이벤트는
            // 다음 레이아웃 패스에서야 발생한다. finally에서 곧바로 플래그를 내리면 그 이벤트가
            // 가드가 풀린 뒤에 도착해 반대편으로 되돌아오는 에코를 막지 못한다.
            // 세로축은 양쪽 줄 수가 항상 같아 우연히 문제가 안 보이지만, 가로축은 두 창의
            // 최대 줄 길이가 서로 달라 좁은 창이 자신의 최대치로 clamp된 값을 되돌려보내면서
            // 넓은 창을 좁은 창의 한계까지 끌어내린다. 그래서 레이아웃이 끝난 뒤(Loaded 우선순위)
            // Dispatcher.BeginInvoke로 미뤄서 내려야 한다 — 이 부분을 finally로 "단순화"하지 말 것.
            Dispatcher.BeginInvoke(new Action(() => _syncingScroll = false), DispatcherPriority.Loaded);
        }
    }
}
