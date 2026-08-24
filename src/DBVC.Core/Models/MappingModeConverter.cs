using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DBVC.Core.Models
{
    /// <summary>
    /// <see cref="MappingMode"/>를 문자열로 읽고 쓴다.
    ///
    /// 기본 열거형 변환기를 쓰지 않는 이유는 실패 방향 때문이다. 기본 변환기는 모르는 문자열에
    /// 예외를 던지고, 그러면 mappings.json 한 줄의 오타가 전체 매핑을 날린다. 여기서는
    /// <see cref="MappingMode.Audit"/>으로 떨어뜨린다 — 오타 때문에 권한이 넓어지는 것보다
    /// 좁아지는 편이 안전하다.
    ///
    /// 속성 자체가 없는 경우(0.2.x가 만든 파일)는 이 변환기가 호출되지 않고 C# 기본값인
    /// <see cref="MappingMode.Write"/>가 남는다. 값이 빠진 것과 값이 틀린 것은 다르게 다뤄야 한다.
    /// </summary>
    internal sealed class MappingModeConverter : JsonConverter<MappingMode>
    {
        public override MappingMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

            return Enum.TryParse<MappingMode>(raw, ignoreCase: true, out var parsed)
                && Enum.IsDefined(typeof(MappingMode), parsed)
                    ? parsed
                    : MappingMode.Audit;
        }

        public override void Write(Utf8JsonWriter writer, MappingMode value, JsonSerializerOptions options)
        {
            // 숫자로 쓰면 사람이 파일을 읽고 고칠 수 없다.
            writer.WriteStringValue(value.ToString());
        }
    }
}
