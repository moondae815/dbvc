using System;
using System.Windows.Controls;
using DBVC.Vsix.Services;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.UI
{
    public partial class ViewChangesControl : UserControl
    {
        private readonly ViewChangesViewModel _viewModel;
        private readonly DiffService _diffService;

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

            _viewModel.SelectionChanged += OnSelectionChanged;
            Unloaded += (_, __) => _viewModel.SelectionChanged -= OnSelectionChanged;
        }

        /// <summary>
        /// 선택된 객체의 Git HEAD 버전과 현재 DB 버전을 좌우 에디터에 채운다.
        /// </summary>
        private void OnSelectionChanged(object? sender, EventArgs e)
        {
            var selected = _viewModel.SelectedChange;
            if (selected == null || _viewModel.ServerName == null || _viewModel.DatabaseName == null)
            {
                OldTextEditor.Text = string.Empty;
                NewTextEditor.Text = string.Empty;
                return;
            }

            // Diff 생성 실패(신규 객체 등)는 빈 쪽으로 자연스럽게 표현되며 예외를 던지지 않는다.
            var (oldText, newText) = _diffService.GetDiffTexts(
                _viewModel.ServerName,
                _viewModel.DatabaseName,
                selected.RelativePath);

            OldTextEditor.Text = oldText;
            NewTextEditor.Text = newText;
        }
    }
}
