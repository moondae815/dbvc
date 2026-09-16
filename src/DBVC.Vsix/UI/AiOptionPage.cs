using System.Runtime.InteropServices;
using System.Windows;
using DBVC.Core;
using DBVC.Vsix.Services;
using Microsoft.VisualStudio.Shell;

namespace DBVC.Vsix.UI
{
    [Guid("6d1b4a72-0f35-4b8e-9c2a-51d7e0a34c18")]
    public class AiOptionPage : UIElementDialogPage
    {
        private AiOptionsControl? _control;

        private IAiSettingsStore Store => DbvcServices.Default.AiSettingsStore;

        protected override UIElement Child => _control ??= new AiOptionsControl();

        /// <summary>
        /// 기본 구현은 속성을 VS 설정 저장소(레지스트리)에 <b>평문으로</b> 남긴다.
        /// 이 두 메서드를 재정의해 저장을 AiSettingsStore 하나로 몰지 않으면
        /// DPAPI 암호화가 통째로 무의미해진다. base를 부르지 않는 것이 요점이다.
        /// </summary>
        public override void LoadSettingsFromStorage()
        {
            ((AiOptionsControl)Child).LoadFrom(Store.Load());
        }

        /// <summary>
        /// AiSettingsStore.Save는 실패를 일부러 삼키지 않는다 — API 키를 입력하고 확인을
        /// 눌렀는데 조용히 사라지면 다음 커밋 때에야(혹은 영영) 알게 된다. 그렇다고 이 예외를
        /// 그대로 VS 셸에 던지면 사용자에게는 영문 스택과 함께 대화상자가 죽는 것으로 보인다 —
        /// 여기서 잡아 한국어 사유로 알려준다.
        /// </summary>
        public override void SaveSettingsToStorage()
        {
            try
            {
                Store.Save(((AiOptionsControl)Child).ToSettings());
            }
            catch (System.Exception ex)
            {
                new MessageBoxNotifier().ShowError("DBVC AI 설정 저장 실패", ex.Message);
            }
        }
    }
}
