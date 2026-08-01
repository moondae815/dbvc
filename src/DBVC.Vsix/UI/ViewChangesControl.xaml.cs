using System;
using System.Windows.Controls;
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
                SetPane(OldTextEditor, _oldRenderer, new DiffPane());
                SetPane(NewTextEditor, _newRenderer, new DiffPane());
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
            try
            {
                target.ScrollToVerticalOffset(source.VerticalOffset);
                target.ScrollToHorizontalOffset(source.HorizontalOffset);
            }
            finally
            {
                _syncingScroll = false;
            }
        }
    }
}
