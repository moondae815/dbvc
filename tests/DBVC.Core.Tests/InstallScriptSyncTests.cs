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
        public void InstallScript_TracksExactlyTheObjectTypesTheConventionKnows_PlusTheParentPointingTypes()
        {
            // INDEX와 COLUMN은 독립 파일이 되지 않으므로 폴더 사전(DdlEventObjectTypes)에는 없다.
            // 부모로 정규화되어야 하므로 기록은 해야 한다 - 그래서 여기서만 더한다.
            var expected = ObjectPathConvention.DdlEventObjectTypes.Concat(new[] { "INDEX", "COLUMN" }).ToArray();

            var lists = TrackedTypeLists();
            Assert.That(lists, Is.Not.Empty, "설치 스크립트에서 DBVC_TRACKED_TYPES 표식을 찾지 못했습니다");

            foreach (var block in lists)
            {
                Assert.That(ParseTypes(block), Is.EquivalentTo(expected),
                    "설치 스크립트의 타입 목록이 ObjectPathConvention과 다릅니다");
            }
        }

        [Test]
        public void InstallScript_StampsTheVersionCoreRequires()
        {
            var script = StateTracker.ReadInstallScript();
            var match = Regex.Match(script, @"@name\s*=\s*N'DBVC_SchemaVersion'\s*,\s*@value\s*=\s*N'(\d+)'");

            Assert.That(match.Success, Is.True, "설치 스크립트에서 DBVC_SchemaVersion 값을 찾지 못했습니다");
            Assert.That(int.Parse(match.Groups[1].Value), Is.EqualTo(StateTracker.RequiredSchemaVersion));
        }

        [Test]
        public void InstallScript_GrantsTheChangeLogAccessTheClientNeeds()
        {
            // 트리거의 INSERT는 EXECUTE AS 'dbo'로 돌지만, 목록 조회와 커밋 후 로그 닫기는
            // 클라이언트가 접속 계정 그대로 한다. 이 GRANT가 없으면 db_owner가 아닌 사용자의
            // 커밋이 로그를 닫지 못해 같은 항목이 새로고침마다 되살아난다.
            var script = StateTracker.ReadInstallScript();

            var match = Regex.Match(
                script,
                @"GRANT\s+([A-Z,\s]+?)\s+ON\s+\[dbo\]\.\[DBVC_ChangeLog\]\s+TO\s+\[public\]",
                RegexOptions.IgnoreCase);

            Assert.That(match.Success, Is.True, "DBVC_ChangeLog에 대한 public GRANT를 찾지 못했습니다");

            var verbs = match.Groups[1].Value
                .Split(',')
                .Select(v => v.Trim().ToUpperInvariant())
                .ToArray();

            // INSERT는 일부러 빼 둔다 - 트리거가 dbo로 쓰므로 필요 없고, 주면 사용자가
            // 로그를 직접 조작할 수 있게 된다.
            Assert.That(verbs, Is.EquivalentTo(new[] { "SELECT", "UPDATE" }));
        }
    }
}
