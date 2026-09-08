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

        /// <summary>
        /// "--"부터 줄 끝까지를 지운다. 배치 순서를 문자열 위치로 검사할 때, 그 규칙을 설명하는
        /// 주석 산문이 같은 키워드(예: "CREATE PROCEDURE [dbo].[DBVC_...]")를 우연히 언급하면
        /// 실제로는 안전한 구간의 주석이 위반으로 잘못 잡히거나, 반대로 진짜 위반을 감싼 주석이
        /// 검사를 가려 결함을 숨길 수 있다. 이 스크립트에는 문자열 리터럴 안에 "--"가 없음을
        /// 확인했다 - 있었다면 이 방식은 안전하지 않다.
        /// </summary>
        private static string StripSqlLineComments(string sql)
        {
            var lines = sql.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var idx = lines[i].IndexOf("--", StringComparison.Ordinal);
                if (idx >= 0) lines[i] = lines[i].Substring(0, idx);
            }
            return string.Join("\n", lines);
        }

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
            //
            // Regex.Match(첫 번째만)가 아니라 Matches를 쓴다 - public이 DBVC_ChangeLog에
            // DELETE를 받는 일이 절대 없다는 것이 이 브랜치의 핵심 보안 주장인데, 첫 번째
            // GRANT 뒤에 같은 테이블에 대한 두 번째 GRANT가 추가되어도 Match만으로는 못 잡는다.
            //
            // 주석을 지우고 본다 - 모든 일치를 검사하게 되면서 주석 속 예시까지 대상이 됐다.
            // 이 파일의 문체상 "GRANT DELETE ...를 주지 않는다" 같은 예시를 주석에 적기 쉬운데,
            // 지우지 않으면 그 산문 한 줄이 실제 권한과 무관하게 테스트를 깨뜨린다.
            var script = StripSqlLineComments(StateTracker.ReadInstallScript());

            var matches = Regex.Matches(
                script,
                @"GRANT\s+([A-Z,\s]+?)\s+ON\s+\[dbo\]\.\[DBVC_ChangeLog\]\s+TO\s+\[public\]",
                RegexOptions.IgnoreCase);

            Assert.That(matches, Is.Not.Empty, "DBVC_ChangeLog에 대한 public GRANT를 찾지 못했습니다");

            foreach (Match match in matches)
            {
                var verbs = match.Groups[1].Value
                    .Split(',')
                    .Select(v => v.Trim().ToUpperInvariant())
                    .ToArray();

                // INSERT는 일부러 빼 둔다 - 트리거가 dbo로 쓰므로 필요 없고, 주면 사용자가
                // 로그를 직접 조작할 수 있게 된다.
                Assert.That(verbs, Is.EquivalentTo(new[] { "SELECT", "UPDATE" }),
                    $"'{match.Value}'가 SELECT, UPDATE 외의 권한을 준다");
            }
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
            //
            // 주석부터 지우고 본다 - 그러지 않으면 이 규칙을 설명하는 주석 문장 자체가 검사
            // 대상 키워드를 언급한다는 이유만으로 오탐하거나(실제로 FIX 1 리뷰 도중 이런
            // 이유로 깨진 적이 있다), 반대로 위반을 감싼 주석이 검사를 가릴 수 있다.
            var script = StripSqlLineComments(StateTracker.ReadInstallScript());

            var dropTrigger = script.IndexOf(
                "DROP TRIGGER [trg_DBVC_DDL_Tracker] ON DATABASE", StringComparison.Ordinal);
            var createTrigger = script.IndexOf(
                "CREATE TRIGGER [trg_DBVC_DDL_Tracker]", StringComparison.Ordinal);

            Assert.That(dropTrigger, Is.GreaterThan(-1), "DROP TRIGGER 문을 찾지 못했습니다");
            Assert.That(createTrigger, Is.GreaterThan(-1), "CREATE TRIGGER 문을 찾지 못했습니다");

            // DROP도 함께 본다 - DROP_PROCEDURE 이벤트의 ObjectName도 프로시저 자신의 이름이라
            // CREATE와 똑같이 옛 트리거를 통과한다. CREATE만 감시하면 짝을 이루는 DROP이 구간
            // 밖으로 새는 것을 못 잡는다.
            //
            // DBVC_ChangeLog만 제외한다. 그 이름은 옛 트리거의 나열 목록에 이미 있어 어디서
            // 만들어도 로그에 남지 않는다(그래서 테이블 생성·ALTER·GRANT가 이 구간 밖에 있다).
            // 기준은 "DBVC가 만드는 것 전부"가 아니라 "옛 목록에 없던 이름"이고, 설치 스크립트의
            // 같은 자리 주석이 같은 기준을 적고 있다.
            var ownedObjectPatterns = new[]
            {
                @"CREATE PROCEDURE \[dbo\]\.\[DBVC_\w+\]",
                @"DROP PROCEDURE \[dbo\]\.\[DBVC_\w+\]",
                @"CREATE NONCLUSTERED INDEX \[IX_DBVC_\w+\]",
                @"GRANT\s+[A-Z]+\s+ON\s+\[dbo\]\.\[DBVC_(?!ChangeLog\])\w+\]",
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
