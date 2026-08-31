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
        /// <summary>.vsct의 guidDbvcPackageCmdSet과 일치해야 한다.</summary>
        public static readonly Guid CommandSet = new Guid("5c9e7b22-1d3f-4a68-b0c4-9e7d5f2a3b14");

        /// <summary>.vsct의 ShowHistoryCommandId와 일치해야 한다.</summary>
        public const int CommandId = 0x0102;

        private readonly DbvcPackage _package;
        private readonly ISsmsConnectionSource _source;

        public ShowHistoryCommand(DbvcPackage package, OleMenuCommandService commandService, ISsmsConnectionSource source)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            if (commandService == null) throw new ArgumentNullException(nameof(commandService));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Execute, menuCommandId);
            menuItem.BeforeQueryStatus += MenuItem_BeforeQueryStatus;
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(DbvcPackage package, ISsmsConnectionSource? source = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            if (await package.GetServiceAsync(typeof(IMenuCommandService)) is not OleMenuCommandService commandService)
            {
                return;
            }

            _ = new ShowHistoryCommand(package, commandService, source ?? new ObjectExplorerConnectionSource());
        }

        private void MenuItem_BeforeQueryStatus(object sender, EventArgs e)
        {
            if (sender is not OleMenuCommand myCommand) return;

            var urn = _source.TryGetSelectedUrn();
            myCommand.Visible = SsmsUrn.TryParseObjectIdentity(urn, out _, out _, out _, out _);
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
