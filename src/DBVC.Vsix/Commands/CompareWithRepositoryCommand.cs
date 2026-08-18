using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using DBVC.Core;
using DBVC.Vsix.Services;
using DBVC.Vsix.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace DBVC.Vsix.Commands
{
    /// <summary>
    /// SQL 에디터 컨텍스트 메뉴의 "DBVC: 저장소 버전과 비교" 명령. (Feature 11, 12)
    /// 커서에서 선택된 객체 이름을 해석해 View Changes 창에서 해당 객체의 Diff를 연다.
    /// </summary>
    internal sealed class CompareWithRepositoryCommand
    {
        /// <summary>.vsct의 guidDbvcPackageCmdSet과 일치해야 한다.</summary>
        public static readonly Guid CommandSet = new Guid("5c9e7b22-1d3f-4a68-b0c4-9e7d5f2a3b14");

        /// <summary>.vsct의 CompareWithRepositoryCommandId와 일치해야 한다.</summary>
        public const int CommandId = 0x0101;

        private readonly DbvcPackage _package;
        private readonly IUserNotifier _notifier;

        private CompareWithRepositoryCommand(DbvcPackage package, OleMenuCommandService commandService, IUserNotifier notifier)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));

            commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
        }

        public static async Task InitializeAsync(DbvcPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            if (await package.GetServiceAsync(typeof(IMenuCommandService)) is not OleMenuCommandService commandService)
            {
                return;
            }

            _ = new CompareWithRepositoryCommand(package, commandService, new MessageBoxNotifier());
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var selection = GetSelectedText();
            if (!ObjectNameResolver.TryParse(selection, out var schema, out var name))
            {
                _notifier.ShowError("DBVC", "비교할 객체 이름을 선택하세요. 예: dbo.Users");
                return;
            }

            var viewModel = _package.Services.SharedViewChangesViewModel;
            if (!viewModel.TrySelectObject(schema, name))
            {
                _notifier.ShowError(
                    "DBVC",
                    $"'{name}'은(는) 현재 변경 목록에 없습니다. 이번 세션에서 아직 연결을 누르지 " +
                    "않았다면 먼저 DBVC 창에서 연결을 누르세요. 이미 접속했다면 새로고침을 실행하세요.");
                return;
            }

            ShowToolWindow();
        }

        private string? GetSelectedText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (Package.GetGlobalService(typeof(SVsTextManager)) is not IVsTextManager textManager)
            {
                return null;
            }

            if (ErrorHandler.Failed(textManager.GetActiveView(1, null, out IVsTextView view)) || view == null)
            {
                return null;
            }

            return ErrorHandler.Failed(view.GetSelectedText(out string selectedText)) ? null : selectedText;
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
