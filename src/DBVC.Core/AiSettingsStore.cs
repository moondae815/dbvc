using System;
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
                catch (JsonException)
                {
                    // 손상된 파일로 화면 전체가 열리지 않는 일은 없어야 한다.
                    return new AiSettings();
                }
                catch (IOException)
                {
                    return new AiSettings();
                }
            }
        }

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
