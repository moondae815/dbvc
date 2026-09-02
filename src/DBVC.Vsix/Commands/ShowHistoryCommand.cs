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
using System.Threading;

namespace DBVC.Vsix.Commands
{
    public sealed class ShowHistoryCommand : IDisposable
    {
        public static readonly Guid CommandSet = new Guid("5c9e7b22-1d3f-4a68-b0c4-9e7d5f2a3b14");
        public const int CommandId = 0x0102;

        private readonly DbvcPackage _package;
        private readonly ISsmsConnectionSource _source;
        private TreeView? _treeView;
        
        private ToolStripMenuItem? _menuItem;
        private ToolStripSeparator? _menuSeparator;
        
        // Polling timer for late initialization
        private System.Windows.Forms.Timer? _retryTimer;

        public ShowHistoryCommand(DbvcPackage package, ISsmsConnectionSource source)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            
            ThreadHelper.ThrowIfNotOnUIThread();
            TryHookTreeView();
            
            if (_treeView == null)
            {
                // If not available immediately, start a polling timer
                _retryTimer = new System.Windows.Forms.Timer();
                _retryTimer.Interval = 2000; // 2 seconds
                _retryTimer.Tick += RetryTimer_Tick;
                _retryTimer.Start();
            }
        }

        private void RetryTimer_Tick(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TryHookTreeView();
            
            if (_treeView != null)
            {
                _retryTimer?.Stop();
                _retryTimer?.Dispose();
                _retryTimer = null;
            }
        }

        private void TryHookTreeView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_treeView != null) return;
            
            try
            {
                var interfacesAssembly = System.Reflection.Assembly.Load("SqlWorkbench.Interfaces");
                var explorerServiceType = interfacesAssembly?.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService");
                var vsIntegrationAssembly = System.Reflection.Assembly.Load("Microsoft.SqlServer.SqlTools.VSIntegration");
                var serviceCacheType = vsIntegrationAssembly?.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
                
                if (explorerServiceType != null && serviceCacheType != null)
                {
                    // Use ServiceCache to reliably get SSMS internal services
                    var serviceProvider = serviceCacheType.GetProperty("ServiceProvider", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) as IServiceProvider;
                    var explorerService = serviceProvider?.GetService(explorerServiceType);
                    
                    // Fallback to global provider if ServiceCache fails
                    if (explorerService == null)
                    {
                        explorerService = ServiceProvider.GlobalProvider.GetService(explorerServiceType);
                    }
                    
                    if (explorerService != null)
                    {
                        var actualType = explorerService.GetType();
                        var treeProperty = actualType.GetProperty("Tree", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        
                        if (treeProperty != null)
                        {
                            _treeView = treeProperty.GetValue(explorerService) as TreeView;
                        }
                        else
                        {
                            // Try to look for a field if property is not found
                            var treeField = actualType.GetField("Tree", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                            if (treeField != null)
                            {
                                _treeView = treeField.GetValue(explorerService) as TreeView;
                            }
                        }
                        
                        if (_treeView != null)
                        {
                            SsmsDiagnostics.Trace($"ShowHistoryCommand: 개체 탐색기 TreeView 훅에 성공했습니다. (실제 타입: {actualType.Name})");
                            _treeView.ContextMenuStripChanged += TreeView_ContextMenuStripChanged;
                            HookContextMenuStrip(_treeView.ContextMenuStrip);
                        }
                        else
                        {
                            var propNames = string.Join(", ", System.Linq.Enumerable.Select(actualType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance), p => p.Name));
                            SsmsDiagnostics.Trace($"ShowHistoryCommand: TreeView를 얻을 수 없습니다. Properties: {propNames}");
                        }
                    }
                    else
                    {
                        SsmsDiagnostics.Trace("ShowHistoryCommand: IObjectExplorerService를 찾지 못했습니다 (ServiceCache 및 GlobalProvider).");
                    }
                }
                else
                {
                    SsmsDiagnostics.Trace("ShowHistoryCommand: 필수 어셈블리 또는 타입을 로드하지 못했습니다.");
                }
            }
            catch (Exception ex)
            {
                SsmsDiagnostics.Trace($"ShowHistoryCommand: TryHookTreeView 중 예외 발생: {ex.Message}");
            }
        }

        public static async Task InitializeAsync(DbvcPackage package, ISsmsConnectionSource? source = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            SsmsDiagnostics.Trace("ShowHistoryCommand: InitializeAsync 시작");
            var connectionSource = source ?? new ObjectExplorerConnectionSource();
            var command = new ShowHistoryCommand(package, connectionSource);
            package.DisposalToken.Register(() => command.Dispose());
        }

        private void TreeView_ContextMenuStripChanged(object sender, EventArgs e)
        {
            if (_treeView == null) return;
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
            if (sender is not ContextMenuStrip menu || _treeView == null) return;

            var pt = _treeView.PointToClient(Cursor.Position);
            var nodeAtMouse = _treeView.GetNodeAt(pt);
            if (nodeAtMouse != null && _treeView.SelectedNode != nodeAtMouse)
            {
                _treeView.SelectedNode = nodeAtMouse;
            }

            var urn = _source.TryGetSelectedUrn();
            bool isObjectNode = SsmsUrn.TryParseObjectIdentity(urn, out _, out _, out _, out _);
            
            SsmsDiagnostics.Trace($"ShowHistoryCommand: ContextMenu 열림. Node={nodeAtMouse?.Text}, isObjectNode={isObjectNode}, Urn={urn}");

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
            if (!SsmsUrn.TryParseObjectIdentity(urn, out _, out var schema, out var objectType, out var objectName))
            {
                return;
            }

            var relativePath = ObjectPathConvention.GetRelativePath(schema, objectType, objectName!);

            // 노드의 연결을 Connect와 같은 경로로 읽는다. URN의 SMO 서버명은 연결 객체의
            // ServerName과 표기가 달라, URN을 파싱해 비교하면 정상 경로까지 막힌다.
            var connection = _source.TryGetCurrent();

            // 안내가 도구 창 배너로 나가므로 실패 경로에서도 창을 먼저 띄운다.
            ShowToolWindow();

            var viewModel = _package.Services.SharedViewChangesViewModel;
            viewModel.ShowHistoryFor(connection?.ServerName, connection?.DatabaseName, relativePath);
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
            if (_retryTimer != null)
            {
                _retryTimer.Stop();
                _retryTimer.Dispose();
                _retryTimer = null;
            }
            
            if (_treeView != null)
            {
                _treeView.ContextMenuStripChanged -= TreeView_ContextMenuStripChanged;
                if (_treeView.ContextMenuStrip != null)
                {
                    _treeView.ContextMenuStrip.Opening -= ContextMenuStrip_Opening;
                }
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
