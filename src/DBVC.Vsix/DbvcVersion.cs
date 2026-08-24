using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Format의 경계 처리(빌드 메타데이터·빈 값)는 화면에 나가는 문자열을 정하는 곳이라
// 테스트로 고정한다. 그것 때문에 public으로 넓히지는 않는다.
[assembly: InternalsVisibleTo("DBVC.Vsix.Tests")]

namespace DBVC.Vsix
{
    /// <summary>
    /// 설치된 확장의 버전. 값의 출처는 source.extension.vsixmanifest 하나뿐이며,
    /// 빌드 시 csproj가 그 값을 AssemblyInformationalVersion으로 흘려 넣는다.
    /// 여기에 숫자를 직접 적으면 릴리스마다 두 곳이 어긋난다.
    /// </summary>
    public static class DbvcVersion
    {
        /// <summary>매니페스트를 읽지 못한 빌드에서도 화면이 빈칸으로 남지 않게 한다.</summary>
        internal const string UnknownLabel = "알 수 없음";

        private static readonly string _current = Format(
            typeof(DbvcVersion).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        /// <summary>"0.2.7" 형태의 표시용 버전.</summary>
        public static string Current => _current;

        /// <summary>
        /// SemVer 빌드 메타데이터를 떼어 낸다. .NET SDK는 SourceLink가 켜지면
        /// InformationalVersion 뒤에 "+커밋해시"를 붙이는데, 사용자에게는 의미가 없다.
        /// </summary>
        internal static string Format(string? informationalVersion)
        {
            if (string.IsNullOrWhiteSpace(informationalVersion)) return UnknownLabel;

            var trimmed = informationalVersion!.Trim();
            var plus = trimmed.IndexOf('+');
            return plus >= 0 ? trimmed.Substring(0, plus) : trimmed;
        }
    }
}
