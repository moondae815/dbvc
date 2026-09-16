using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 도구 > 옵션 > DBVC > AI 커밋 메시지의 화면.
    ///
    /// API 키를 PropertyGrid에 노출하지 않으려고 UIElementDialogPage를 쓴다 —
    /// 기본 DialogPage는 속성을 그대로 그려 키가 평문으로 보인다.
    /// </summary>
    public partial class AiOptionsControl : UserControl
    {
        public AiOptionsControl()
        {
            InitializeComponent();
        }

        public void LoadFrom(AiSettings settings)
        {
            BaseUrlBox.Text = settings.BaseUrl;
            ModelBox.Text = settings.Model;
            ApiKeyBox.Password = settings.ApiKey;
            TimeoutBox.Text = settings.TimeoutSeconds.ToString();
            MaxDiffLinesBox.Text = settings.MaxDiffLines.ToString();
            _consentedHost = settings.ConsentedHost;
        }

        private string? _consentedHost;

        public AiSettings ToSettings()
        {
            return new AiSettings
            {
                BaseUrl = BaseUrlBox.Text?.Trim() ?? string.Empty,
                Model = ModelBox.Text?.Trim() ?? string.Empty,
                ApiKey = ApiKeyBox.Password ?? string.Empty,
                TimeoutSeconds = ParsePositive(TimeoutBox.Text, 30),
                MaxDiffLines = ParsePositive(MaxDiffLinesBox.Text, 400),
                // 동의 기록은 화면이 만들지 않는다. 주소가 바뀌면 AiConsent가 알아서 다시 묻는다.
                ConsentedHost = _consentedHost,
            };
        }

        private static int ParsePositive(string? text, int fallback) =>
            int.TryParse(text, out var value) && value > 0 ? value : fallback;

        /// <summary>
        /// 세 값이 모두 맞아야 동작하는데, 틀렸다는 것을 커밋하려던 순간에 처음 알면 안 된다.
        /// </summary>
        private void OnTestConnection(object sender, RoutedEventArgs e)
        {
            var settings = ToSettings();
            if (!settings.IsConfigured)
            {
                TestResultText.Text = "프로바이더 주소와 모델을 먼저 입력하세요.";
                return;
            }

            TestButton.IsEnabled = false;
            TestResultText.Text = "확인 중...";

            var client = new OpenAiCompatibleClient();
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await client.CompleteAsync(settings, "ping", "ping", CancellationToken.None);
                    return "연결에 성공했습니다.";
                }
                catch (AiRequestException ex)
                {
                    return ex.Message;
                }
                catch (Exception ex)
                {
                    return "연결에 실패했습니다.\n\n" + ex.Message;
                }
            }).ContinueWith(task =>
            {
                TestResultText.Text = task.Result;
                TestButton.IsEnabled = true;
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
