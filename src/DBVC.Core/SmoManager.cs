using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        public ScriptResult? ScriptObjectsDetailed(string serverName, string databaseName, List<string>? objectNames = null)
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
                Debug.WriteLine($"Config error in GetMapping for '{serverName}.{databaseName}': {ex.Message}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(localGitPath))
            {
                Debug.WriteLine($"'{serverName}.{databaseName}'에 매핑된 Git 저장소가 없습니다.");
                return null;
            }

            try
            {
                var connStr = _connectionFactory.Build(serverName, databaseName);

                using var sqlConn = new SqlConnection(connStr);
                var conn = new ServerConnection(sqlConn);
                var server = new Server(conn);
                var db = server.Databases[databaseName];

                if (db == null)
                {
                    Debug.WriteLine($"Database '{databaseName}' not found on server '{serverName}'.");
                    return null;
                }

                var scripter = new Scripter(server)
                {
                    Options = new ScriptingOptions
                    {
                        ScriptDrops = false,
                        IncludeIfNotExists = false,
                        ToFileOnly = true,
                        AppendToFile = false
                    }
                };

                var filter = BuildFilter(objectNames);
                var targets = EnumerateTargets(db).Where(t => ShouldInclude(t, filter));

                return ScriptAll(targets, localGitPath!, (target, outputPath) =>
                {
                    scripter.Options.FileName = outputPath;
                    scripter.Script(new[] { (Urn)target.Tag! });
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during SMO scripting for '{serverName}.{databaseName}': {ex}");
                return null;
            }
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
            Action<ScriptTargetInfo, string> scriptOne)
        {
            var result = new ScriptResult();

            var stagingDir = Path.Combine(Path.GetTempPath(), "dbvc_smo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            try
            {
                var index = 0;
                foreach (var target in targets)
                {
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
                    yield return NewTarget(table.Schema, trigger.Name, "Trigger", trigger.Urn);
                }
            }

            foreach (View view in db.Views)
            {
                if (view.IsSystemObject) continue;
                yield return NewTarget(view.Schema, view.Name, "View", view.Urn);
            }

            foreach (StoredProcedure sp in db.StoredProcedures)
            {
                if (sp.IsSystemObject) continue;
                yield return NewTarget(sp.Schema, sp.Name, "StoredProcedure", sp.Urn);
            }

            foreach (UserDefinedFunction fn in db.UserDefinedFunctions)
            {
                if (fn.IsSystemObject) continue;
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
