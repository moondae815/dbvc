using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using DBVC.Vsix.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DBVC.Vsix.Commands
{
    /// <summary>
    /// View 메뉴에서 "DBVC View Changes" 도구 창을 여는 명령.
    /// </summary>
    internal sealed class ViewChangesCommand
    {
        /// <summary>.vsct의 guidDbvcPackageCmdSet과 일치해야 한다.</summary>
        public static readonly Guid CommandSet = new Guid("5c9e7b22-1d3f-4a68-b0c4-9e7d5f2a3b14");

        /// <summary>.vsct의 ViewChangesCommandId와 일치해야 한다.</summary>
        public const int CommandId = 0x0100;

        private readonly DbvcPackage _package;

        private ViewChangesCommand(DbvcPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            if (commandService == null) throw new ArgumentNullException(nameof(commandService));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, menuCommandId));
        }

        public static async Task InitializeAsync(DbvcPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                return;
            }

            _ = new ViewChangesCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var window = _package.FindToolWindow(typeof(ViewChangesToolWindow), 0, create: true);
            if (window?.Frame == null)
            {
                throw new NotSupportedException("DBVC View Changes 도구 창을 만들 수 없습니다.");
            }

            var frame = (IVsWindowFrame)window.Frame;
            ErrorHandler.ThrowOnFailure(frame.Show());
        }
    }
}
