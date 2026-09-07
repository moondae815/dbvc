using System;
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

        [Test]
        public void InstallScript_PurgesAfterTheRetentionCoreDeclares()
        {
            // 두 값이 갈라지면 문서와 실제 동작이 달라진다 - 30일이라 적어 놓고 90일에 지운다.
            var script = StateTracker.ReadInstallScript();
            var match = Regex.Match(script, @"DATEADD\(day,\s*-(\d+),\s*GETDATE\(\)\)");

            Assert.That(match.Success, Is.True, "설치 스크립트에서 보존 기간을 찾지 못했습니다");
            Assert.That(int.Parse(match.Groups[1].Value), Is.EqualTo(StateTracker.RetentionDays));
        }

        [Test]
        public void InstallScript_CreatesEveryDbvcOwnedObjectOnlyWhileNoTriggerIsLive()
        {
            // 인덱스 하나(IX_DBVC_ChangeLog_PostTime)만 이름으로 박아 두면, 다음에 추가되는
            // DBVC 소유 객체(프로시저·인덱스·GRANT)가 같은 자리를 벗어나도 이 테스트는 통과한다.
            // 실제로 DBVC_PurgeChangeLog 프로시저가 그렇게 놓쳤다 - 규칙 자체를 검사해야 한다.
            //
            // v5 -> v6 같은 재설치에서는 옛 트리거가 이 시점에 아직 살아있을 수 있다(DROP 후
            // CREATE 전). 살아있는 동안 DBVC 객체를 만들면 CREATE/GRANT 이벤트의 ObjectName이
            // 그 객체 자신의 이름이라 옛 트리거의 자기 제외 판정(문자열 나열, DBVC_ 접두사 규칙
            // 없음)을 피해 가고, ObjectType(INDEX/PROCEDURE)은 추적 대상이라 로그에 남는다.
            // 안전한 자리는 DROP TRIGGER와 CREATE TRIGGER 사이뿐이다 - 벗어나면 여기서 잡는다.
            var script = StateTracker.ReadInstallScript();

            var dropTrigger = script.IndexOf(
                "DROP TRIGGER [trg_DBVC_DDL_Tracker] ON DATABASE", StringComparison.Ordinal);
            var createTrigger = script.IndexOf(
                "CREATE TRIGGER [trg_DBVC_DDL_Tracker]", StringComparison.Ordinal);

            Assert.That(dropTrigger, Is.GreaterThan(-1), "DROP TRIGGER 문을 찾지 못했습니다");
            Assert.That(createTrigger, Is.GreaterThan(-1), "CREATE TRIGGER 문을 찾지 못했습니다");

            var ownedObjectPatterns = new[]
            {
                @"CREATE PROCEDURE \[dbo\]\.\[DBVC_\w+\]",
                @"CREATE NONCLUSTERED INDEX \[IX_DBVC_\w+\]",
                @"GRANT\s+[A-Z]+\s+ON\s+\[dbo\]\.\[DBVC_Purge\w*\]",
            };

            var matches = ownedObjectPatterns
                .SelectMany(pattern => Regex.Matches(script, pattern).Cast<Match>())
                .ToArray();

            // 검사 대상을 하나도 못 찾으면 아래 검사가 공허하게 통과한다 - 그것도 실패로 본다.
            Assert.That(matches, Is.Not.Empty, "DBVC 소유 객체 생성문을 하나도 찾지 못했습니다");

            var offenders = matches
                .Where(m => m.Index <= dropTrigger || m.Index >= createTrigger)
                .Select(m => $"'{m.Value}' (위치 {m.Index})")
                .ToArray();

            Assert.That(offenders, Is.Empty,
                "DBVC 소유 객체는 DROP TRIGGER와 CREATE TRIGGER 사이에서만 만들어야 합니다. " +
                "자리를 벗어난 문장: " + string.Join(", ", offenders));
        }

        [Test]
        public void InstallScript_ExcludesTheSameObjectsCoreCallsItsOwn()
        {
            // 트리거는 SQL이라 DbvcOwnedObjects를 부를 수 없다. 두 판정이 갈라지면
            // 도구가 자기 DDL을 사용자 변경으로 기록하고, 그것이 저장소에 커밋된다.
            var script = StateTracker.ReadInstallScript();

            // "DBVC_" → "DBVC[_]" → N'DBVC[_]%'. 밑줄은 LIKE의 와일드카드라 이스케이프한다.
            var expectedPattern = "N'" + DbvcOwnedObjects.Prefix.Replace("_", "[_]") + "%'";

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain(expectedPattern),
                    "설치 스크립트의 접두사 패턴이 DbvcOwnedObjects.Prefix와 다릅니다");
                Assert.That(script, Does.Contain("N'" + DbvcOwnedObjects.TriggerName + "'"),
                    "설치 스크립트가 DDL 트리거 이름을 제외 목록에 두지 않았습니다");
            });
        }
    }
}
