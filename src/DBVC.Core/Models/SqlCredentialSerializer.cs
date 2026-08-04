using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DBVC.Core.Models
{
    /// <summary>
    /// <see cref="SqlCredential"/> 목록을 credentials.json 형식으로 직렬화한다.
    /// </summary>
    internal static class SqlCredentialSerializer
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            // AuthMode를 0/1이 아니라 "Windows"/"Sql"로 적는다.
            // 사용자가 파일을 열어 확인할 일이 있고, 숫자는 의미를 알 수 없다.
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        public static string Serialize(IReadOnlyList<SqlCredential> credentials)
        {
            return JsonSerializer.Serialize(credentials, Options);
        }

        public static List<SqlCredential>? Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            return JsonSerializer.Deserialize<List<SqlCredential>>(json, Options);
        }
    }
}
