using System.Collections.Generic;
using System.Text.Json;

namespace DBVC.Core.Models
{
    /// <summary>
    /// <see cref="MappingConfig"/> 목록을 mappings.json 형식으로 직렬화한다.
    /// </summary>
    internal static class MappingConfigSerializer
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static string Serialize(IReadOnlyList<MappingConfig> mappings)
        {
            return JsonSerializer.Serialize(mappings, Options);
        }

        public static List<MappingConfig>? Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            return JsonSerializer.Deserialize<List<MappingConfig>>(json, Options);
        }
    }
}
