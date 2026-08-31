using System;
using System.ComponentModel.Design;
using DBVC.Core;
using DBVC.Vsix.Services;
using DBVC.Vsix.UI;
using DBVC.Vsix.ViewModels;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;
using System.Windows.Forms;

namespace DBVC.Vsix.Commands
{
    /// <summary>
    /// SSMS 개체 탐색기 컨텍스트 메뉴의 "DBVC: 이력 보기" 명령.
    /// 선택한 개체(테이블, 뷰 등)의 URN을 파싱하여 View Changes 창에서 해당 객체의 이력을 단일 객체 모드로 연다.
    /// </summary>
    public sealed class ShowHistoryCommand : IDisposable
    {
        // 단위 테스트 호환성 유지를 위한 상수
        public static readonly Guid CommandSet = new Guid("5c9e7b22-1d3f-4a68-b0c4-9e7d5f2a3b14");
        public const int CommandId = 0x0102;

        private readonly DbvcPackage _package;
        private readonly ISsmsConnectionSource _source;
        private readonly TreeView _treeView;
        
        private ToolStripMenuItem? _menuItem;
        private ToolStripSeparator? _menuSeparator;

        public ShowHistoryCommand(DbvcPackage package, ISsmsConnectionSource source, TreeView treeView)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _treeView = treeView ?? throw new ArgumentNullException(nameof(treeView));

            _treeView.ContextMenuStripChanged += TreeView_ContextMenuStripChanged;
            HookContextMenuStrip(_treeView.ContextMenuStrip);
        }

        public static async Task InitializeAsync(DbvcPackage package, ISsmsConnectionSource? source = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var connectionSource = source ?? new ObjectExplorerConnectionSource();

            try
            {
                var interfacesAssembly = System.Reflection.Assembly.Load("SqlWorkbench.Interfaces");
                var explorerServiceType = interfacesAssembly?.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService");
                
                if (explorerServiceType != null)
                {
                    var explorerService = await package.GetServiceAsync(explorerServiceType);
                    if (explorerService != null)
                    {
                        var treeProperty = explorerServiceType.GetProperty("Tree", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        var treeView = treeProperty?.GetValue(explorerService) as TreeView;
                        if (treeView != null)
                        {
                            var command = new ShowHistoryCommand(package, connectionSource, treeView);
                            package.DisposalToken.Register(() => command.Dispose());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShowHistoryCommand.InitializeAsync failed to hook TreeView: {ex.Message}");
            }
        }

        private void TreeView_ContextMenuStripChanged(object sender, EventArgs e)
        {
            HookContextMenuStrip(_treeView.ContextMenuStrip);
        }

        private void HookContextMenuStrip(ContextMenuStrip? menu)
        {
            if (menu == null) return;
            menu.Opening -= ContextMenuStrip_Opening;
            menu.Opening += ContextMenuStrip_Opening;
        }

        private void ContextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (sender is not ContextMenuStrip menu) return;

            // 마우스 위치를 기반으로 현재 우클릭한 노드 강제 선택 시도
            var pt = _treeView.PointToClient(Cursor.Position);
            var nodeAtMouse = _treeView.GetNodeAt(pt);
            if (nodeAtMouse != null && _treeView.SelectedNode != nodeAtMouse)
            {
                _treeView.SelectedNode = nodeAtMouse;
            }

            var urn = _source.TryGetSelectedUrn();
            bool isObjectNode = SsmsUrn.TryParseObjectIdentity(urn, out _, out _, out _, out _);

            if (_menuItem == null)
            {
                _menuItem = new ToolStripMenuItem("DBVC: 이력 보기") { Name = "DbvcShowHistoryMenuItem" };
                _menuItem.Click += Execute;
                _menuSeparator = new ToolStripSeparator() { Name = "DbvcShowHistoryMenuSeparator" };
            }

            if (isObjectNode)
            {
                if (!menu.Items.Contains(_menuItem))
                {
                    menu.Items.Add(_menuSeparator);
                    menu.Items.Add(_menuItem);
                }
            }
            else
            {
                if (menu.Items.Contains(_menuItem))
                {
                    menu.Items.Remove(_menuSeparator);
                    menu.Items.Remove(_menuItem);
                }
            }
        }

        public void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var urn = _source.TryGetSelectedUrn();
            if (!SsmsUrn.TryParseObjectIdentity(urn, out var databaseName, out var schema, out var objectType, out var objectName))
            {
                return;
            }

            var relativePath = ObjectPathConvention.GetRelativePath(schema, objectType, objectName!);

            ShowToolWindow();

            var viewModel = _package.Services.SharedViewChangesViewModel;
            viewModel.ShowHistoryFor(databaseName!, relativePath);
        }

        private void ShowToolWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var window = _package.FindToolWindow(typeof(ViewChangesToolWindow), 0, create: true);
            if (window?.Frame is IVsWindowFrame frame)
            {
                ErrorHandler.ThrowOnFailure(frame.Show());
            }
        }

        public void Dispose()
        {
            _treeView.ContextMenuStripChanged -= TreeView_ContextMenuStripChanged;
            if (_treeView.ContextMenuStrip != null)
            {
                _treeView.ContextMenuStrip.Opening -= ContextMenuStrip_Opening;
            }
            if (_menuItem != null)
            {
                _menuItem.Click -= Execute;
                _menuItem.Dispose();
            }
            if (_menuSeparator != null)
            {
                _menuSeparator.Dispose();
            }
        }
    }
}
