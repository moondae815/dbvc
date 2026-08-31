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

namespace DBVC.Vsix.Commands
{
    /// <summary>
    /// SSMS 개체 탐색기 컨텍스트 메뉴의 "DBVC: 이력 보기" 명령.
    /// 선택한 개체(테이블, 뷰 등)의 URN을 파싱하여 View Changes 창에서 해당 객체의 이력을 단일 객체 모드로 연다.
    /// </summary>
    internal sealed class ShowHistoryCommand
    {
        private readonly DbvcPackage _package;
        private readonly ISsmsConnectionSource _source;
        private System.Windows.Forms.TreeView? _treeView;

        public ShowHistoryCommand(DbvcPackage package, ISsmsConnectionSource source, System.Windows.Forms.TreeView treeView)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _treeView = treeView ?? throw new ArgumentNullException(nameof(treeView));

            _treeView.ContextMenuStripChanged += TreeView_ContextMenuStripChanged;
        }

        public static async Task InitializeAsync(DbvcPackage package, ISsmsConnectionSource? source = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var connectionSource = source ?? new ObjectExplorerConnectionSource();

            try
            {
                // IObjectExplorerService를 리플렉션으로 가져오지 않고, VSSDK GetService로 가져온 뒤 Tree 속성을 읽는다.
                // 서비스 타입은 SqlWorkbench.Interfaces.IObjectExplorerService 이다.
                // 직접 참조하지 않으므로 이름을 통해 찾는다.
                var interfacesAssembly = System.Reflection.Assembly.Load("SqlWorkbench.Interfaces");
                var explorerServiceType = interfacesAssembly?.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService");
                
                if (explorerServiceType != null)
                {
                    var explorerService = await package.GetServiceAsync(explorerServiceType);
                    if (explorerService != null)
                    {
                        var treeProperty = explorerServiceType.GetProperty("Tree", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        var treeView = treeProperty?.GetValue(explorerService) as System.Windows.Forms.TreeView;
                        if (treeView != null)
                        {
                            _ = new ShowHistoryCommand(package, connectionSource, treeView);
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
            if (_treeView?.ContextMenuStrip == null) return;

            // 중복 방지
            if (_treeView.ContextMenuStrip.Items.ContainsKey("DbvcShowHistoryMenuItem")) return;

            var urn = _source.TryGetSelectedUrn();
            if (!SsmsUrn.TryParseObjectIdentity(urn, out var databaseName, out var schema, out var objectType, out var objectName))
            {
                return;
            }

            var menuItem = new System.Windows.Forms.ToolStripMenuItem("DBVC: 이력 보기")
            {
                Name = "DbvcShowHistoryMenuItem"
            };
            menuItem.Click += Execute;

            _treeView.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator() { Name = "DbvcShowHistoryMenuSeparator" });
            _treeView.ContextMenuStrip.Items.Add(menuItem);
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
    }
}
