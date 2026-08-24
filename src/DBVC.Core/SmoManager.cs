using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DBVC.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;

namespace DBVC.Core
{
    /// <summary>
    /// SMO를 사용해 데이터베이스 객체의 CREATE 스크립트를 로컬 저장소로 추출한다.
    /// </summary>
    public class SmoManager : ISmoManager
    {
        private readonly IConfigManager _configManager;
        private readonly SqlConnectionFactory _connectionFactory;

        public SmoManager(IConfigManager? configManager = null)
            : this(configManager, null)
        {
        }

        public SmoManager(IConfigManager? configManager, ISqlCredentialStore? credentialStore)
        {
            _configManager = configManager ?? new ConfigManager();
            _connectionFactory = new SqlConnectionFactory(credentialStore);
        }

        /// <summary>
        /// 대상 DB의 객체를 <c>[Schema]/[ObjectType]/[Name].sql</c> 구조로 추출한다.
        /// </summary>
        /// <param name="objectNames">
        /// 추출할 객체 이름. <c>dbo.Users</c>처럼 스키마를 한정하거나 이름만 줄 수 있다.
        /// <c>null</c>이거나 비어 있으면 지원되는 모든 객체를 추출한다.
        /// </param>
        public bool ScriptObjects(string serverName, string databaseName, List<string>? objectNames = null)
        {
            return ScriptObjectsDetailed(serverName, databaseName, objectNames) != null;
        }

        /// <summary>
        /// <see cref="ScriptObjects"/>와 동일하지만 성공/실패 객체 수를 함께 반환한다.
        /// 연결 실패 등으로 스크립팅을 시작조차 못한 경우 <c>null</c>을 반환한다.
        /// </summary>
        public ScriptResult? ScriptObjectsDetailed(
            string serverName,
            string databaseName,
            List<string>? objectNames = null,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return null;
            }

            string? localGitPath;
            try
            {
                localGitPath = _configManager.GetMapping(serverName, databaseName);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Config error in GetMapping for '{serverName}.{databaseName}': {ex.Message}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(localGitPath))
            {
                Trace.WriteLine($"'{serverName}.{databaseName}'에 매핑된 Git 저장소가 없습니다.");
                return null;
            }

            try
            {
                var connStr = _connectionFactory.Build(serverName, databaseName);

                using var sqlConn = new SqlConnection(connStr);
                var conn = new ServerConnection(sqlConn);
                var server = new Server(conn);
                ConfigureBulkEnumeration(server);
                var db = server.Databases[databaseName];

                if (db == null)
                {
                    Trace.WriteLine($"Database '{databaseName}' not found on server '{serverName}'.");
                    return null;
                }

                var scripter = new Scripter(server) { Options = BuildScriptingOptions() };

                var filter = BuildFilter(objectNames);
                var targets = EnumerateTargets(db).Where(t => ShouldInclude(t, filter));

                return ScriptAll(targets, localGitPath!, (target, outputPath) =>
                {
                    scripter.Options.FileName = outputPath;
                    scripter.Script(new[] { (Urn)target.Tag! });
                }, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 사용자가 멈춘 것이다. null로 뭉개면 호출자가 "추출 실패"로 알린다.
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error during SMO scripting for '{serverName}.{databaseName}': {ex}");
                return null;
            }
        }

        /// <summary>
        /// 스크립팅 옵션. 지정하지 않은 값은 SMO 기본값을 따르는데, 테이블에 관계된 것은
        /// <b>전부 false</b>다 — 그래서 켜기 전까지 테이블 .sql에 컬럼 정의만 남았다.
        /// 기본값 제약도 기본 키도 인덱스도 없는 파일로는 배포 스크립트가 테이블을 재생산하지
        /// 못한다. 저장소가 테이블의 실제 모습을 담는 것이 형상 관리의 전제이므로 켠다.
        ///
        /// <see cref="ScriptingOptions.DriAll"/>로 묶어 켠다. 개별 Dri* 를 나열하면 SMO가 항목을
        /// 더할 때 조용히 빠지는 것이 생긴다. 인덱스는 <see cref="ScriptingOptions.Indexes"/>
        /// 하나에 기대지 않고 종류별로 명시한다.
        ///
        /// <b>켜지 않는 것.</b> Permissions는 서버마다 로그인과 역할이 달라 저장소를 환경 종속으로
        /// 만들고, 배포 스크립트에 들어가면 대상 환경에 없는 주체를 참조해 실패한다. Statistics는
        /// 데이터 분포의 부산물이라 같은 스키마에서도 매번 달라져 잡음 diff가 된다.
        ///
        /// 이 옵션들은 스크립팅 단계의 조회를 늘린다. 열거 단계를 다루는
        /// <see cref="ConfigureBulkEnumeration"/>의 실측 튜닝과는 층이 다르므로 그쪽은 건드리지 않는다.
        /// 비용이 드러나는 곳은 새로고침이 아니라 전체 다시 추출이다.
        /// </summary>
        internal static ScriptingOptions BuildScriptingOptions()
        {
            return new ScriptingOptions
            {
                ScriptDrops = false,
                IncludeIfNotExists = false,
                ToFileOnly = true,
                AppendToFile = false,

                DriAll = true,

                Indexes = true,
                ClusteredIndexes = true,
                NonClusteredIndexes = true,
                XmlIndexes = true,
                FullTextIndexes = true,

                ExtendedProperties = true,

                // 저장소 파일 자체가 실행 가능해야 한다. 배포 스크립트의 재료는 브랜치의 파일이지
                // 대상 DB에서 다시 뜬 것이 아니므로(설계 2.3), 여기서 CREATE OR ALTER로 쓰지 않으면
                // 생성 시점에 텍스트를 치환해야 하고 그러면 주석·문자열 안의 CREATE까지 건드린다.
                // 테이블에는 적용되지 않는다 - T-SQL에 CREATE OR ALTER TABLE이 없다.
                // 반드시 ScriptDrops 뒤에 와야 한다 - SMO의 ScriptDrops setter가 값과 무관하게
                // ScriptForCreateOrAlter를 꺼버리는 부작용이 있다(리플렉션 없이 실측으로 확인).
                // 객체 초기화 구문은 나열한 순서대로 세터를 호출하므로 순서 자체가 정확성의 일부다.
                ScriptForCreateOrAlter = true
            };
        }

        /// <summary>
        /// 컬렉션을 열거할 때 SMO가 무엇을 미리 가져올지 정한다.
        ///
        /// SMO는 기본적으로 컬렉션을 열거할 때 식별 정보만 읽고, 지정되지 않은 속성을 처음
        /// 만지는 순간 <b>그 객체 하나를 위해</b> 전체 속성 집합을 다시 조회한다. 여기에는
        /// 행 수·사용 공간처럼 비싼 것도 들어 있다. <see cref="EnumerateTargets"/>는 객체마다
        /// IsSystemObject를 읽으므로 그 조회가 객체 수만큼 일어난다.
        ///
        /// localhost SQL Server 2022에서 실측한 값이다(객체 200개짜리 DB):
        ///   지정 없음   : 저장 프로시저 1개당 2842 ms — 컬렉션 전체로 환산하면 72분
        ///   IsSystemObject만 지정 : 열거 871 ms, 속성 접근 0 ms
        ///
        /// 필드를 더 넣으면 오히려 나빠진다. 같은 조건에서 Schema·Name을 덧붙이면 열거가
        /// 13359 ms, 해당 타입의 전체 필드를 켜면 78568 ms였다. Schema와 Name은 이미 식별
        /// 정보로 함께 오므로, 여기에 적는 순간 기본 집합을 더 무거운 것으로 바꿔 버린다.
        /// <b>추가하지 말 것.</b>
        ///
        /// 여기 없는 타입(UserDefinedType, Sequence, Synonym 등)은 스키마와 이름만 읽는데
        /// 둘 다 식별 정보라 별도 조회를 유발하지 않는다.
        /// </summary>
        private static void ConfigureBulkEnumeration(Server server)
        {
            server.SetDefaultInitFields(typeof(Table), "IsSystemObject");
            server.SetDefaultInitFields(typeof(View), "IsSystemObject");
            server.SetDefaultInitFields(typeof(StoredProcedure), "IsSystemObject");
            server.SetDefaultInitFields(typeof(UserDefinedFunction), "IsSystemObject");
            server.SetDefaultInitFields(typeof(Trigger), "IsSystemObject");
        }

        /// <summary>
        /// 대상 객체들을 하나씩 스크립팅한다.
        /// 설계 3.1에 따라 개별 객체의 실패는 격리되어 전체 프로세스를 중단시키지 않는다.
        ///
        /// 스크립트는 작업 트리 밖의 임시 파일에 먼저 쓰고, 기존 파일과 바이트가 다를 때만
        /// 옮긴다. 내용이 같은데도 덮어쓰면 파일의 mtime이 바뀌고, 그러면 libgit2의 status가
        /// 인덱스에 캐시된 stat 정보를 믿지 못해 추적 파일 전부를 다시 읽어 해시한다 —
        /// 객체 3000개 기준으로 status 한 번이 18ms에서 6.6초가 된다. DBVC는 새로고침마다
        /// 전 객체를 추출하므로 이 비용이 매번 붙는다.
        ///
        /// 임시 파일을 작업 트리 안에 두지 않는 이유는 두 가지다 — git이 미추적 파일로 잡아
        /// 변경 목록을 오염시키고, 스크립팅이 중간에 실패하면 반쯤 쓰인 파일이 남는다.
        /// </summary>
        internal static ScriptResult ScriptAll(
            IEnumerable<ScriptTargetInfo> targets,
            string localGitPath,
            Action<ScriptTargetInfo, string> scriptOne,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = new ScriptResult();

            // 진행률에 분모를 붙이려면 대상 수를 먼저 알아야 한다. 열거는 SetDefaultInitFields로
            // 이미 싼 연산이 되었으므로(ConfigureBulkEnumeration 참고) 여기서 확정해도 된다.
            var targetList = targets as System.Collections.Generic.IReadOnlyList<ScriptTargetInfo> ?? targets.ToList();

            var stagingDir = Path.Combine(Path.GetTempPath(), "dbvc_smo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            try
            {
                var index = 0;
                foreach (var target in targetList)
                {
                    // 취소는 되돌리기가 아니다. 여기까지 옮겨 둔 파일은 그대로 둔다 —
                    // 지우면 다음 새로고침이 그만큼을 다시 해야 한다.
                    cancellationToken.ThrowIfCancellationRequested();

                    var stagingPath = Path.Combine(stagingDir, (index++).ToString() + ".sql");

                    try
                    {
                        var outputPath = Path.Combine(
                            localGitPath,
                            target.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                        scriptOne(target, stagingPath);
                        PublishIfChanged(stagingPath, outputPath);
                        result.SucceededCount++;
                    }
                    catch (Exception ex)
                    {
                        // 객체 하나의 실패가 나머지 객체 추출을 막아서는 안 된다.
                        Debug.WriteLine($"Failed to script '{target.QualifiedName}': {ex.Message}");
                        result.FailedObjects.Add(target.QualifiedName);
                    }
                    finally
                    {
                        TryDelete(stagingPath);

                        // 실패한 객체도 센다. 그러지 않으면 실패 지점에서 진행이 멈춘 것처럼 보인다.
                        progress?.Report(new ExtractionProgress(index, targetList.Count, target.QualifiedName));
                    }
                }
            }
            finally
            {
                try { Directory.Delete(stagingDir, recursive: true); }
                catch (Exception ex) { Debug.WriteLine($"Failed to remove staging dir '{stagingDir}': {ex.Message}"); }
            }

            return result;
        }

        /// <summary>
        /// 갓 추출한 파일을 최종 경로에 반영한다. 바이트가 같으면 아무것도 하지 않는다.
        /// </summary>
        private static void PublishIfChanged(string stagingPath, string outputPath)
        {
            if (HasSameBytes(stagingPath, outputPath)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // File.Move의 덮어쓰기 오버로드는 netstandard2.0에 없다.
            // 임시 디렉터리와 저장소가 다른 볼륨에 있으면 Move도 어차피 복사가 된다.
            File.Copy(stagingPath, outputPath, overwrite: true);
        }

        private static bool HasSameBytes(string stagingPath, string outputPath)
        {
            if (!File.Exists(outputPath)) return false;

            var stagingInfo = new FileInfo(stagingPath);
            var outputInfo = new FileInfo(outputPath);
            if (stagingInfo.Length != outputInfo.Length) return false;

            // 추출물은 객체 하나의 DDL이라 통째로 읽어도 부담이 없다.
            var staging = File.ReadAllBytes(stagingPath);
            var output = File.ReadAllBytes(outputPath);

            for (var i = 0; i < staging.Length; i++)
            {
                if (staging[i] != output[i]) return false;
            }

            return true;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove staging file '{path}': {ex.Message}");
            }
        }

        internal static HashSet<string>? BuildFilter(List<string>? objectNames)
        {
            if (objectNames == null || objectNames.Count == 0) return null;

            return new HashSet<string>(
                objectNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        internal static bool ShouldInclude(ScriptTargetInfo target, HashSet<string>? filter)
        {
            if (filter == null) return true;
            return filter.Contains(target.QualifiedName) || filter.Contains(target.Name);
        }

        /// <summary>
        /// Feature 14가 요구하는 9개 객체 타입을 열거한다.
        /// </summary>
        private static IEnumerable<ScriptTargetInfo> EnumerateTargets(Database db)
        {
            foreach (Table table in db.Tables)
            {
                if (table.IsSystemObject) continue;
                yield return NewTarget(table.Schema, table.Name, "Table", table.Urn);

                // DML 트리거는 부모 테이블 밑에 있으며 부모의 스키마를 따른다.
                foreach (Trigger trigger in table.Triggers)
                {
                    if (trigger.IsSystemObject) continue;
                    DisableTextMode(trigger);
                    yield return NewTarget(table.Schema, trigger.Name, "Trigger", trigger.Urn);
                }
            }

            foreach (View view in db.Views)
            {
                if (view.IsSystemObject) continue;
                DisableTextMode(view);
                yield return NewTarget(view.Schema, view.Name, "View", view.Urn);
            }

            foreach (StoredProcedure sp in db.StoredProcedures)
            {
                if (sp.IsSystemObject) continue;
                DisableTextMode(sp);
                yield return NewTarget(sp.Schema, sp.Name, "StoredProcedure", sp.Urn);
            }

            foreach (UserDefinedFunction fn in db.UserDefinedFunctions)
            {
                if (fn.IsSystemObject) continue;
                DisableTextMode(fn);
                yield return NewTarget(fn.Schema, fn.Name, "UserDefinedFunction", fn.Urn);
            }

            foreach (UserDefinedType udt in db.UserDefinedTypes)
            {
                yield return NewTarget(udt.Schema, udt.Name, "UserDefinedType", udt.Urn);
            }

            foreach (UserDefinedDataType uddt in db.UserDefinedDataTypes)
            {
                yield return NewTarget(uddt.Schema, uddt.Name, "UserDefinedDataType", uddt.Urn);
            }

            foreach (UserDefinedTableType udtt in db.UserDefinedTableTypes)
            {
                yield return NewTarget(udtt.Schema, udtt.Name, "UserDefinedTableType", udtt.Urn);
            }

            foreach (Sequence sequence in db.Sequences)
            {
                yield return NewTarget(sequence.Schema, sequence.Name, "Sequence", sequence.Urn);
            }

            foreach (Synonym synonym in db.Synonyms)
            {
                yield return NewTarget(synonym.Schema, synonym.Name, "Synonym", synonym.Urn);
            }
        }

        /// <summary>
        /// <see cref="ScriptingOptions.ScriptForCreateOrAlter"/>는 그 자체로는 아무것도 바꾸지 않는다
        /// - 프로시저·뷰·함수·트리거는 기본적으로 <c>TextMode = true</c>라 SMO가 sys.sql_modules에
        /// 저장된 원문을 그대로 돌려주고, 그 안의 CREATE 키워드는 옵션과 무관하게 원본 그대로 남는다.
        /// 서버에 직접 붙어 실측해서 찾은 값이다(문서에는 이 의존관계가 안 나온다). TextMode를 꺼야
        /// SMO가 메타데이터로 헤더를 다시 조립하고, 그 조립 단계에서만 CREATE OR ALTER가 반영된다.
        /// 부작용으로 대괄호 식별자·WITH EXECUTE AS 같은 절이 다시 채워져 원문 서식과 달라지는데,
        /// 이는 이 작업(#4)이 저장소의 모든 파일 텍스트를 갈아엎는 근본 이유이기도 하다.
        /// </summary>
        private static void DisableTextMode(ITextObject obj) => obj.TextMode = false;

        private static ScriptTargetInfo NewTarget(string schema, string name, string objectType, Urn urn)
        {
            return new ScriptTargetInfo
            {
                Schema = schema,
                Name = name,
                ObjectType = objectType,
                Tag = urn
            };
        }
    }
}
