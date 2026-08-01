using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DBVC.Vsix.Commands;
using DBVC.Vsix.UI;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DBVC.Vsix
{
    /// <summary>
    /// DBVC VSIX 진입점. SSMS 21(Visual Studio 2022 Shell)에 도구 창과 메뉴 명령을 등록한다.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ViewChangesToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    public sealed class DbvcPackage : AsyncPackage
    {
        public const string PackageGuidString = "3f2a1c40-8b16-4d1e-9a5e-2b7c6d4e9f01";

        /// <summary>
        /// 도구 창과 명령이 공유하는 코어 매니저들.
        /// </summary>
        public DbvcServices Services => DbvcServices.Default;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // 명령 등록은 UI 스레드에서 이루어져야 한다.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await ViewChangesCommand.InitializeAsync(this);
        }
    }
}
