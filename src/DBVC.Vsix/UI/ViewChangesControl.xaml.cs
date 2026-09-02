using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DBVC.Vsix.Services;
using DBVC.Vsix.ViewModels;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DBVC.Vsix.UI
{
    public partial class ViewChangesControl : UserControl
    {
        private readonly ViewChangesViewModel _viewModel;
        private readonly DiffService _diffService;
        private readonly DiffLineBackgroundRenderer _oldRenderer;
        private readonly DiffLineBackgroundRenderer _newRenderer;
        private readonly DiffLineBackgroundRenderer _deployLeftRenderer;
        private readonly DiffLineBackgroundRenderer _deployRightRenderer;
        private readonly DiffLineBackgroundRenderer _historyOldRenderer;
        private readonly DiffLineBackgroundRenderer _historyNewRenderer;
        private bool _syncingScroll;

        // UpdateHistoryRowHeights가 접힘↔펼침이 실제로 바뀔 때만 Height를 쓰기 위한 상태.
        // null은 "아직 한 번도 적용 안 함"이라 첫 호출은 항상 실행된다. 접기 직전 값을
        // *ExpandedHeight에 남겨 둬야 GridSplitter로 사용자가 끌어 둔 비율이 펼칠 때
        // 1*로 리셋되지 않고 되돌아온다.
        private bool? _changedFilesCollapsed;
        private bool? _historyDiffCollapsed;
        private GridLength _changedFilesExpandedHeight = new GridLength(1, GridUnitType.Star);
        private GridLength _historyDiffExpandedHeight = new GridLength(1, GridUnitType.Star);

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

            // 배포·감사 패널의 diff 쌍. 위와 짝을 맞추지 않으면 줄 배경색이 한쪽만 빠지는 채로
            // 남는다 - 렌더러를 붙이는 자리가 둘로 갈라져 있어 실제로 그런 식으로 놓치기 쉽다.
            _deployLeftRenderer = new DiffLineBackgroundRenderer(DeployLeftEditor.TextArea.TextView);
            _deployRightRenderer = new DiffLineBackgroundRenderer(DeployRightEditor.TextArea.TextView);
            DeployLeftEditor.TextArea.TextView.BackgroundRenderers.Add(_deployLeftRenderer);
            DeployRightEditor.TextArea.TextView.BackgroundRenderers.Add(_deployRightRenderer);

            _historyOldRenderer = new DiffLineBackgroundRenderer(HistoryOldEditor.TextArea.TextView);
            _historyNewRenderer = new DiffLineBackgroundRenderer(HistoryNewEditor.TextArea.TextView);
            HistoryOldEditor.TextArea.TextView.BackgroundRenderers.Add(_historyOldRenderer);
            HistoryNewEditor.TextArea.TextView.BackgroundRenderers.Add(_historyNewRenderer);

            OldTextEditor.TextArea.TextView.ScrollOffsetChanged += OnOldScrollOffsetChanged;
            NewTextEditor.TextArea.TextView.ScrollOffsetChanged += OnNewScrollOffsetChanged;
            HistoryOldEditor.TextArea.TextView.ScrollOffsetChanged += OnHistoryOldScrollOffsetChanged;
            HistoryNewEditor.TextArea.TextView.ScrollOffsetChanged += OnHistoryNewScrollOffsetChanged;

            _viewModel.SelectionChanged += OnSelectionChanged;
            _viewModel.Deployment.SelectionChanged += OnDeploymentSelectionChanged;
            // 이 구독은 일부러 Unloaded에서 해제하지 않는다. Unloaded는 도구 창을 다시 도킹할
            // 때도 뜨는데(비주얼 트리에서 빠졌다 다시 붙는 것뿐), 여기서 해제하면 그 뒤로는
            // 세션이 끝날 때까지 개체 탐색기 선택 확인(OnIsVisibleChanged 등)이 조용히 멈춘다.
            // 핸들러는 리소스를 들고 있지 않고
            // 호출 비용도 낮으므로 컨트롤 수명 내내 살려 둔다.
            IsVisibleChanged += OnIsVisibleChanged;

            // 도구 창이 계속 보이는 채로 개체 탐색기 선택만 바뀌면 위 이벤트는 뜨지 않는다.
            // 사용자가 이 패널로 시선을 옮기는 순간에 확인해, 선택이 달라졌으면 알린다.
            // 위와 같은 이유로 Unloaded에서 해제하지 않는다.
            MouseEnter += OnPointerOrFocusEntered;
            GotKeyboardFocus += OnPointerOrFocusEntered;

            // 위 구독들은 Unloaded에서 해제되므로 다시 붙을 때 되살려야 한다.
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>
        /// 비주얼 트리에 다시 붙을 때 구독을 되살린다.
        ///
        /// <see cref="OnUnloaded"/>의 해제를 없애는 것으로는 해결되지 않는다. 공유
        /// <see cref="ViewChangesViewModel"/>은 이 컨트롤보다 오래 살기 때문에, 해제하지 않으면
        /// 창을 닫아 버려진 컨트롤을 ViewModel이 계속 붙들게 된다. 그래서 해제는 남기고
        /// 재구독을 여기에 둔다 — 짝을 맞추지 않으면 재도킹 뒤로 Diff 창이 선택을 따라오지 않고
        /// 좌우 스크롤도 어긋난다.
        ///
        /// Loaded는 재도킹마다 다시 뜨므로 <c>-=</c>로 한 번 걷어내고 건다. 구독하지 않은
        /// 핸들러를 빼는 것은 무해하며, 이 방식이라면 생성자의 최초 구독과도 겹치지 않는다.
        /// </summary>
        private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _viewModel.SelectionChanged -= OnSelectionChanged;
            _viewModel.SelectionChanged += OnSelectionChanged;
            _viewModel.Deployment.SelectionChanged -= OnDeploymentSelectionChanged;
            _viewModel.Deployment.SelectionChanged += OnDeploymentSelectionChanged;
            _viewModel.History.PropertyChanged -= OnHistoryPropertyChanged;
            _viewModel.History.PropertyChanged += OnHistoryPropertyChanged;

            OldTextEditor.TextArea.TextView.ScrollOffsetChanged -= OnOldScrollOffsetChanged;
            OldTextEditor.TextArea.TextView.ScrollOffsetChanged += OnOldScrollOffsetChanged;
            NewTextEditor.TextArea.TextView.ScrollOffsetChanged -= OnNewScrollOffsetChanged;
            NewTextEditor.TextArea.TextView.ScrollOffsetChanged += OnNewScrollOffsetChanged;

            HistoryOldEditor.TextArea.TextView.ScrollOffsetChanged -= OnHistoryOldScrollOffsetChanged;
            HistoryOldEditor.TextArea.TextView.ScrollOffsetChanged += OnHistoryOldScrollOffsetChanged;
            HistoryNewEditor.TextArea.TextView.ScrollOffsetChanged -= OnHistoryNewScrollOffsetChanged;
            HistoryNewEditor.TextArea.TextView.ScrollOffsetChanged += OnHistoryNewScrollOffsetChanged;

            // 떨어져 있는 동안 선택이 바뀌었을 수 있다 — SQL 편집기 컨텍스트 메뉴가 창 밖에서
            // 같은 ViewModel을 조작한다. 다시 붙는 김에 Diff 창을 현재 선택에 맞춘다.
            // UpdateHistoryDiffView가 끝에서 UpdateHistoryRowHeights를 부르므로 여기서 또
            // 부르지 않는다 - 두 자리에서 부르면 어느 쪽이 실제로 화면을 결정하는지 흐려진다.
            OnSelectionChanged(this, EventArgs.Empty);
            UpdateHistoryDiffView();
        }

        private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _viewModel.SelectionChanged -= OnSelectionChanged;
            _viewModel.Deployment.SelectionChanged -= OnDeploymentSelectionChanged;
            _viewModel.History.PropertyChanged -= OnHistoryPropertyChanged;

            OldTextEditor.TextArea.TextView.ScrollOffsetChanged -= OnOldScrollOffsetChanged;
            NewTextEditor.TextArea.TextView.ScrollOffsetChanged -= OnNewScrollOffsetChanged;

            HistoryOldEditor.TextArea.TextView.ScrollOffsetChanged -= OnHistoryOldScrollOffsetChanged;
            HistoryNewEditor.TextArea.TextView.ScrollOffsetChanged -= OnHistoryNewScrollOffsetChanged;
        }

        /// <summary>
        /// 도구 창이 보여질 때 개체 탐색기 선택을 현재 대상과 대조한다.
        /// 처음 열 때와 다른 탭에서 돌아올 때를 함께 덮는다.
        ///
        /// 채울 입력란이 없으므로 여기서 할 수 있는 일은 안내를 맞춰 두는 것뿐이다.
        /// 접속은 언제나 사용자가 Connect를 눌러야 일어난다.
        /// </summary>
        private void OnIsVisibleChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool visible && visible)
            {
                _viewModel.CheckSsmsSelection();
            }
        }

        /// <summary>
        /// 패널에 마우스가 들어오거나 포커스가 올 때 개체 탐색기 선택을 현재 대상과 대조한다.
        ///
        /// 대상을 건드리지 않는다 — 전환은 Connect 버튼이 한다. 그래서
        /// 지나가던 마우스가 대상을 바꿀 일이 없다.
        /// </summary>
        private void OnPointerOrFocusEntered(object sender, System.Windows.RoutedEventArgs e)
        {
            _viewModel.CheckSsmsSelection();
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

            ApplyDiffPanes(model, OldTextEditor, _oldRenderer, NewTextEditor, _newRenderer);
        }

        /// <summary>
        /// 배포·감사 패널에서 선택된 객체 하나를 다시 뜬다. 비교 결과(ComparisonResult)는
        /// 텍스트를 들고 있지 않다 - 객체 수천 개분을 메모리에 쌓지 않으려고 Compare 시점에 버렸다.
        /// </summary>
        private void OnDeploymentSelectionChanged(object? sender, EventArgs e)
        {
            // 콜백은 UI 스레드에서 돌아온다. 여기서 동기로 읽으면 클릭 한 번마다 SMO 접속과
            // DB 전체 열거가 셸을 붙잡는다.
            _viewModel.Deployment.LoadSelectedTexts((branchText, databaseText) =>
            {
                var model = _diffService.GetDiffModelFromString(branchText, databaseText);
                ApplyDiffPanes(model, DeployLeftEditor, _deployLeftRenderer, DeployRightEditor, _deployRightRenderer);
            });
        }

        private void OnHistoryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ObjectHistoryViewModel.SelectedDiffModel))
            {
                UpdateHistoryDiffView();
            }
            else if (e.PropertyName == nameof(ObjectHistoryViewModel.IsSingleObjectMode)
                  || e.PropertyName == nameof(ObjectHistoryViewModel.IsDiffVisible))
            {
                UpdateHistoryRowHeights();
            }
        }

        private void UpdateHistoryDiffView()
        {
            var model = _viewModel.History.SelectedDiffModel;
            if (model != null)
            {
                ApplyDiffPanes(model, HistoryOldEditor, _historyOldRenderer, HistoryNewEditor, _historyNewRenderer);
            }
            else
            {
                SetPane(HistoryOldEditor, _historyOldRenderer, DiffTextBuilder.Build(null));
                SetPane(HistoryNewEditor, _historyNewRenderer, DiffTextBuilder.Build(null));
            }

            UpdateHistoryRowHeights();
        }

        /// <summary>
        /// 필터 모드에서는 변경 파일 목록 행을, 볼 Diff가 없으면 Diff 행을 접는다.
        /// Visibility만으로는 RowDefinition이 자리를 지켜 빈 칸이 화면 1/3을 그대로 차지한다.
        /// RowDefinition은 시각 트리 밖이라 DataContext가 없어 Height에 바인딩을 걸 수 없다 —
        /// 그래서 여기서 준다.
        ///
        /// Height는 접힘↔펼침이 실제로 바뀔 때만 쓴다. GridSplitter도 같은 RowDefinition.Height로
        /// 드래그를 반영하는데, 이 메서드는 UpdateHistoryDiffView를 거쳐 커밋을 하나 고를
        /// 때마다도 불린다 - 상태가 그대로인데 무조건 1*로 다시 쓰면 사용자가 방금 끌어 둔
        /// 분할선이 클릭 한 번마다 원래 비율로 튄다. 접기 직전 값을 기억해 뒀다가 펼칠 때
        /// 그 값으로 되돌리는 것도 같은 이유다 - 그냥 1*로 리셋하면 드래그가 소용없어진다.
        /// </summary>
        private void UpdateHistoryRowHeights()
        {
            var zero = new GridLength(0);
            var splitterRowHeight = new GridLength(5);

            var single = _viewModel.History.IsSingleObjectMode;
            if (_changedFilesCollapsed != single)
            {
                if (single)
                {
                    _changedFilesExpandedHeight = ChangedFilesRow.Height;
                    ChangedFilesRow.Height = zero;
                    ChangedFilesSplitterRow.Height = zero;
                    ChangedFilesPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ChangedFilesRow.Height = _changedFilesExpandedHeight;
                    ChangedFilesSplitterRow.Height = splitterRowHeight;
                    ChangedFilesPanel.Visibility = Visibility.Visible;
                }

                _changedFilesCollapsed = single;
            }

            var hasDiff = _viewModel.History.IsDiffVisible;
            var diffCollapsed = !hasDiff;
            if (_historyDiffCollapsed != diffCollapsed)
            {
                if (diffCollapsed)
                {
                    _historyDiffExpandedHeight = HistoryDiffRow.Height;
                    HistoryDiffRow.Height = zero;
                    HistoryDiffPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    HistoryDiffRow.Height = _historyDiffExpandedHeight;
                    HistoryDiffPanel.Visibility = Visibility.Visible;
                }

                _historyDiffCollapsed = diffCollapsed;
            }

            // 분할선은 접힌 행 쪽으로는 끌리면 안 된다 - 자기 위치가 아니라 양옆 행을 리사이즈하는
            // PreviousAndNext 동작이라, 접힌 빈 칸이 있으면 그 안으로 끌어 들이는 손잡이가 된다.
            // Visibility는 GridSplitter가 손대는 값이 아니므로 매번 다시 써도 사용자 입력을 잃지 않는다.
            HistoryListSplitter.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
            ChangedFilesSplitter.Visibility = (single || diffCollapsed) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HistoryListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (FindVisualAncestor<ListViewItem>(e.OriginalSource as DependencyObject) == null)
            {
                return;
            }

            var selected = _viewModel.History.SelectedEntry;
            var diffModel = _viewModel.History.SelectedDiffModel;
            if (selected == null || diffModel == null) return;

            var oldLines = diffModel.OldText.Lines.Where(l => l.Type != ChangeType.Imaginary);
            var newLines = diffModel.NewText.Lines.Where(l => l.Type != ChangeType.Imaginary);

            var oldText = string.Join(Environment.NewLine, oldLines.Select(l => l.Text ?? string.Empty));
            var newText = string.Join(Environment.NewLine, newLines.Select(l => l.Text ?? string.Empty));

            var tempOld = Path.Combine(Path.GetTempPath(), $"DBVC_{Guid.NewGuid():N}_old.sql");
            var tempNew = Path.Combine(Path.GetTempPath(), $"DBVC_{Guid.NewGuid():N}_new.sql");

            File.WriteAllText(tempOld, oldText);
            File.WriteAllText(tempNew, newText);

            var diffService = Package.GetGlobalService(typeof(SVsDifferenceService)) as IVsDifferenceService;

            if (diffService != null)
            {
                var relativePath = _viewModel.History.SelectedChangedFile?.RelativePath ?? _viewModel.History.RelativePath;
                var leftLabel = selected.HasParent
                    ? $"{relativePath} ({selected.ShortSha}^)"
                    : $"{relativePath} (최초 커밋 이전)";

                diffService.OpenComparisonWindow2(
                    tempOld, tempNew,
                    leftLabel,
                    $"{relativePath} ({selected.ShortSha})",
                    $"DBVC Commit: {selected.ShortSha}",
                    $"DBVC Commit: {selected.ShortSha}",
                    "DBVC", string.Empty, 0);
            }
        }

        private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                {
                    return match;
                }

                if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                else if (current is FrameworkContentElement fce)
                {
                    current = fce.Parent;
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }
            return null;
        }

        /// <summary>
        /// Diff 모델의 좌우를 각 에디터에 채운다. 비교 화면과 배포 화면이 이 한 곳을 같이 써야
        /// 줄 배경 렌더러 부착이 한쪽에만 남는 사고(diff 색이 한쪽 패널에서만 빠지는 것)가
        /// 재발하지 않는다.
        /// </summary>
        private static void ApplyDiffPanes(
            SideBySideDiffModel model,
            TextEditor leftEditor, DiffLineBackgroundRenderer leftRenderer,
            TextEditor rightEditor, DiffLineBackgroundRenderer rightRenderer)
        {
            SetPane(leftEditor, leftRenderer, DiffTextBuilder.Build(model.OldText.Lines));
            SetPane(rightEditor, rightRenderer, DiffTextBuilder.Build(model.NewText.Lines));
        }

        /// <summary>텍스트를 먼저 넣고 줄 종류를 넘긴다. 순서가 반대면 이전 종류로 한 번 그린다.</summary>
        private static void SetPane(TextEditor editor, DiffLineBackgroundRenderer renderer, DiffPane pane)
        {
            editor.Text = pane.Text;
            renderer.SetLineKinds(pane.LineKinds);
        }

        private void OnOldScrollOffsetChanged(object? sender, EventArgs e) => SyncScroll(OldTextEditor, NewTextEditor);

        private void OnNewScrollOffsetChanged(object? sender, EventArgs e) => SyncScroll(NewTextEditor, OldTextEditor);

        private void OnHistoryOldScrollOffsetChanged(object? sender, EventArgs e) => SyncScroll(HistoryOldEditor, HistoryNewEditor);

        private void OnHistoryNewScrollOffsetChanged(object? sender, EventArgs e) => SyncScroll(HistoryNewEditor, HistoryOldEditor);

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
