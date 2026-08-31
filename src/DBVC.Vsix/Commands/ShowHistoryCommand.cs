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
                        var treeProperty = explorerServiceType.GetProperty("Tree", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        _treeView = treeProperty?.GetValue(explorerService) as TreeView;
                        
                        if (_treeView != null)
                        {
                            System.Diagnostics.Debug.WriteLine("DBVC: ShowHistoryCommand successfully hooked Object Explorer TreeView.");
                            _treeView.ContextMenuStripChanged += TreeView_ContextMenuStripChanged;
                            HookContextMenuStrip(_treeView.ContextMenuStrip);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DBVC: TryHookTreeView failed: {ex.Message}");
            }
        }

        public static async Task InitializeAsync(DbvcPackage package, ISsmsConnectionSource? source = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
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
