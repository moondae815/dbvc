using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBVC.Core.Models;

namespace DBVC.Core
{
    public interface IAiSettingsStore
    {
        AiSettings Load();
        void Save(AiSettings settings);
    }

    /// <summary>
    /// AI 설정을 <c>%APPDATA%\DBVC\ai-settings.json</c>에 둔다.
    ///
    /// <c>mappings.json</c>과 다른 파일인 것이 중요하다. 매핑은 (서버, DB)마다 다르고
    /// 팀원끼리 내용을 주고받는 일이 실제로 있다 — 같은 파일에 API 키가 들어 있으면 그때 딸려 나간다.
    /// </summary>
    public class AiSettingsStore : IAiSettingsStore
    {
        private readonly string _filePath;
        private readonly ISecretProtector _protector;
        private readonly object _fileLock = new object();

        public static string DefaultFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBVC",
            "ai-settings.json");

        public AiSettingsStore() : this(DefaultFilePath, new DpapiSecretProtector())
        {
        }

        public AiSettingsStore(string filePath, ISecretProtector protector)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty.", nameof(filePath));
            _filePath = filePath;
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        }

        public string FilePath => _filePath;

        public AiSettings Load()
        {
            lock (_fileLock)
            {
                if (!File.Exists(_filePath)) return new AiSettings();

                try
                {
                    var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(_filePath));
                    if (payload == null) return new AiSettings();

                    return new AiSettings
                    {
                        // 파일이 있으면 그 내용이 권위다. 기본값(AiSettings.DefaultBaseUrl)으로
                        // 되돌리지 않는 이유는, 주소를 일부러 비운 사용자는 AI를 쓰지 않겠다고
                        // 정한 것이기 때문이다 — 기본값은 아직 정하지 않은 사람을 위한 것이지
                        // 정한 사람의 선택을 덮는 것이 아니다.
                        BaseUrl = payload.BaseUrl ?? string.Empty,
                        Model = payload.Model ?? string.Empty,
                        // 복원하지 못해도 나머지 설정은 살린다. 키만 다시 넣으면 되는 상황에서
                        // 전부 기본값으로 돌아가면 사용자는 무엇이 사라졌는지 알 수 없다.
                        ApiKey = string.IsNullOrEmpty(payload.ProtectedApiKey)
                            ? string.Empty
                            : _protector.Unprotect(payload.ProtectedApiKey!) ?? string.Empty,
                        TimeoutSeconds = payload.TimeoutSeconds > 0 ? payload.TimeoutSeconds : 30,
                        MaxDiffLines = payload.MaxDiffLines > 0 ? payload.MaxDiffLines : 400,
                        ConsentedHost = payload.ConsentedHost,
                    };
                }
                catch (Exception ex)
                {
                    // 손상된 파일, 다른 프로세스의 잠금, 권한 거부(UnauthorizedAccessException은
                    // IOException이 아니다) — 무엇이든 설정 파일 하나 때문에 도구 창이 열리지
                    // 않는 일은 없어야 한다. 실제 버그를 삼키지는 않도록 자취는 남긴다.
                    Debug.WriteLine($"AiSettingsStore.Load failed for '{_filePath}': {ex.Message}");
                    return new AiSettings();
                }
            }
        }

        // ConfigManager.Save()와 다르게 여기서는 예외를 삼키지 않는다 — 의도한 비대칭이다.
        // 사용자가 옵션 화면에서 API 키를 입력하고 확인을 눌렀는데 쓰기가 조용히 실패하면,
        // 다음 커밋 때가 되어서야 (혹은 영영) 그 사실을 알게 된다. 호출자(옵션 화면, 다른
        // 작업에서 구현)가 이 예외를 잡아 한국어로 사유를 보여줘야 한다. 여기서 삼켜 조용한
        // 실패로 되돌리지 않는다.
        public void Save(AiSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var payload = new Payload
            {
                BaseUrl = settings.BaseUrl,
                Model = settings.Model,
                ProtectedApiKey = string.IsNullOrEmpty(settings.ApiKey)
                    ? null
                    : _protector.Protect(settings.ApiKey),
                TimeoutSeconds = settings.TimeoutSeconds,
                MaxDiffLines = settings.MaxDiffLines,
                ConsentedHost = settings.ConsentedHost,
            };

            lock (_fileLock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

                File.WriteAllText(
                    _filePath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        /// <summary>디스크 표현. 키 항목의 이름을 <c>protectedApiKey</c>로 둔 것은 의도다 —
        /// 파일을 연 사람이 평문을 기대하지 않게 한다.</summary>
        private sealed class Payload
        {
            [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
            [JsonPropertyName("model")] public string? Model { get; set; }
            [JsonPropertyName("protectedApiKey")] public string? ProtectedApiKey { get; set; }
            [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; }
            [JsonPropertyName("maxDiffLines")] public int MaxDiffLines { get; set; }
            [JsonPropertyName("consentedHost")] public string? ConsentedHost { get; set; }
        }
    }
}
