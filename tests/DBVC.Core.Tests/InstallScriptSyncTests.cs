using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 설치 스크립트(SQL)와 Core(C#)에 같은 목록이 두 벌 있다. 한쪽만 고치면 조용히 어긋나
    /// 파일 없는 항목이 목록에 뜨거나 변경이 통째로 감지되지 않는다. 여기서 죽게 만든다.
    /// </summary>
    [TestFixture]
    public class InstallScriptSyncTests
    {
        /// <summary>
        /// 표식이 붙은 지점부터 다음 세미콜론까지를 한 덩어리로 본다. 트리거의 화이트리스트와
        /// 마이그레이션 UPDATE 두 곳에 같은 표식이 붙으므로 결과는 둘이다.
        /// </summary>
        private static IReadOnlyList<string> TrackedTypeLists()
        {
            var script = StateTracker.ReadInstallScript();
            var results = new List<string>();

            foreach (Match marker in Regex.Matches(script, "DBVC_TRACKED_TYPES"))
            {
                var rest = script.Substring(marker.Index);
                var end = rest.IndexOf(';');
                results.Add(end > 0 ? rest.Substring(0, end) : rest);
            }

            return results;
        }

        private static string[] ParseTypes(string block)
            => Regex.Matches(block, @"N'([^']+)'").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();

        [Test]
        public void InstallScript_TracksExactlyTheObjectTypesTheConventionKnows_PlusIndex()
        {
            var expected = ObjectPathConvention.DdlEventObjectTypes.Concat(new[] { "INDEX" }).ToArray();

            var lists = TrackedTypeLists();
            Assert.That(lists, Is.Not.Empty, "설치 스크립트에서 DBVC_TRACKED_TYPES 표식을 찾지 못했습니다");

            foreach (var block in lists)
            {
                Assert.That(ParseTypes(block), Is.EquivalentTo(expected),
                    "설치 스크립트의 타입 목록이 ObjectPathConvention과 다릅니다");
            }
        }
    }
}
