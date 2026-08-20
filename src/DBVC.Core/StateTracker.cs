using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using DBVC.Core.Models;
using Microsoft.Data.SqlClient;

[assembly: InternalsVisibleTo("DBVC.Core.Tests")]

namespace DBVC.Core
{
    /// <summary>
    /// <c>DBVC_ChangeLog</c>의 DDL 이벤트와 로컬 Git 저장소 상태를 종합해
    /// 객체별 변경 상태 캐시를 관리한다. (설계 3.3)
    /// </summary>
    public class StateTracker : IStateTracker
    {
        /// <summary>설치 스크립트가 심는 스키마 버전. 이 값보다 낮으면 도구 창이 업데이트를 안내한다.</summary>
        public const int RequiredSchemaVersion = 2;

        /// <summary>
        /// 설치 상태를 한 번의 왕복으로 판정한다.
        /// 0 = 미설치, 1 = 버전 표식이 없던 시절의 설치, 그 외 = 심어진 값.
        /// </summary>
        internal const string InstalledVersionQuery = @"
SELECT CASE
    WHEN NOT EXISTS (SELECT 1 FROM sys.objects
                     WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND type = N'U')
      OR NOT EXISTS (SELECT 1 FROM sys.triggers
                     WHERE parent_class = 0 AND name = N'trg_DBVC_DDL_Tracker')
    THEN 0
    ELSE ISNULL((SELECT TRY_CAST(CAST(value AS NVARCHAR(50)) AS int)
                 FROM sys.extended_properties
                 WHERE class = 1 AND major_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]')
                   AND minor_id = 0 AND name = N'DBVC_SchemaVersion'), 1)
END";

        /// <summary>
        /// 아직 처리(커밋)되지 않은 DDL 이벤트만 최신순으로 읽는다.
        /// </summary>
        internal const string PendingChangesQuery = @"
SELECT Id, SchemaName, ObjectName, ObjectType, EventType, TargetObjectName, TargetObjectType
FROM dbo.DBVC_ChangeLog
WHERE IsProcessed = 0
ORDER BY PostTime DESC, Id DESC";

        /// <summary>
        /// 커밋된 객체의 로그 행을 닫는다. TargetObjectName까지 보는 이유는 정규화 때문이다 -
        /// 레코드의 이름은 부모 테이블인데 인덱스 행의 ObjectName은 인덱스 이름이라,
        /// ObjectName만 보면 그 행이 영원히 열린 채로 남아 매번 다시 올라온다.
        /// </summary>
        internal const string MarkProcessedCommand = @"
UPDATE dbo.DBVC_ChangeLog
SET IsProcessed = 1
WHERE IsProcessed = 0 AND Id <= @lastLogId
  AND (ObjectName = @objectName OR TargetObjectName = @objectName)
  AND (ISNULL(SchemaName, N'dbo') = @schemaName)";

        private readonly IConfigManager _configManager;
        private readonly IGitManager _gitManager;
        private readonly SqlConnectionFactory _connectionFactory;

        /// <summary>서버/DB 단위의 변경 목록 캐시. UI 스레드 밖에서 갱신될 수 있어 thread-safe 구조를 쓴다.</summary>
        private readonly ConcurrentDictionary<string, IReadOnlyList<ChangeRecord>> _changesByDatabase =
            new ConcurrentDictionary<string, IReadOnlyList<ChangeRecord>>(StringComparer.OrdinalIgnoreCase);

        public StateTracker() : this(new ConfigManager())
        {
        }

        public StateTracker(IConfigManager configManager) : this(configManager, null)
        {
        }

        public StateTracker(IConfigManager configManager, IGitManager? gitManager)
            : this(configManager, gitManager, null)
        {
        }

        public StateTracker(IConfigManager configManager, IGitManager? gitManager, ISqlCredentialStore? credentialStore)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _gitManager = gitManager ?? new GitManager(_configManager);
            _connectionFactory = new SqlConnectionFactory(credentialStore);
        }

        // ---------- 초기화 ----------

        /// <summary>
        /// 설치된 스키마 버전을 반환한다. 접속 실패는 0으로 알린다 — 사유는 <see cref="TestConnection"/>이
        /// 따로 만들며, 여기서 구분하면 호출자가 같은 배너를 두 곳에서 채우게 된다.
        /// </summary>
        public int GetInstalledVersion(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName)) return 0;
            try
            {
                using var conn = new SqlConnection(_connectionFactory.Build(serverName, databaseName));
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = InstalledVersionQuery;
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StateTracker.GetInstalledVersion failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 임베디드 리소스로 포함된 DBVC 설치 스크립트를 읽는다.
        /// </summary>
        internal static string ReadInstallScript()
        {
            using var stream = typeof(StateTracker).Assembly.GetManifestResourceStream("InstallTrigger.sql");
            if (stream == null) throw new FileNotFoundException("InstallTrigger.sql not found in embedded resources.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// T-SQL 스크립트를 <c>GO</c> 배치 구분자 기준으로 분리한다.
        /// 객체 이름이나 문자열 리터럴에 포함된 GO와 구분하기 위해 단독 행만 구분자로 취급한다.
        /// </summary>
        internal static IReadOnlyList<string> SplitSqlBatches(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return new List<string>();

            var parts = System.Text.RegularExpressions.Regex.Split(
                script,
                @"^\s*GO\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

            var batches = new List<string>();
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                batches.Add(part.Trim());
            }
            return batches;
        }

        /// <summary>
        /// 설치 스크립트를 실행해 ChangeLog 테이블과 DDL 트리거를 만든다.
        /// 권한 부족 등의 실패는 호출자가 사용자에게 알릴 수 있도록 그대로 전파한다.
        /// </summary>
        public void InitializeDatabase(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) throw new ArgumentException("Invalid server name", nameof(serverName));
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Invalid database name", nameof(databaseName));

            var batches = SplitSqlBatches(ReadInstallScript());

            using var conn = new SqlConnection(_connectionFactory.Build(serverName, databaseName));
            conn.Open();
            foreach (var batch in batches)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = batch;
                cmd.ExecuteNonQuery();
            }
        }

        private string BuildConnectionString(string serverName, string databaseName)
        {
            return _connectionFactory.Build(serverName, databaseName);
        }

        /// <summary>
        /// 실제로 접속을 시도해 본다. 성공하면 <c>null</c>, 실패하면 사용자에게 그대로 보여줄
        /// 한국어 사유를 반환한다.
        ///
        /// <see cref="GetInstalledVersion"/>는 "접속 실패"와 "초기화 안 됨"을 모두 0으로 뭉개므로,
        /// SQL 인증 암호가 틀렸을 때 사용자가 원인을 알 방법이 없다. 그 구분을 여기서 만든다.
        /// </summary>
        public string? TestConnection(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return "접속 대상(서버·데이터베이스)이 정해지지 않았습니다.";
            }

            string connectionString;
            try
            {
                connectionString = BuildConnectionString(serverName, databaseName);
            }
            catch (SqlCredentialException ex)
            {
                return ex.Message;
            }

            try
            {
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                return null;
            }
            catch (SqlException ex)
            {
                // 18456은 로그인 실패다. libgit2 쪽과 같은 원칙으로, 가장 흔한 원인을 짚어 준다.
                if (ex.Number == 18456)
                {
                    return $"'{serverName}'에 로그인하지 못했습니다. 사용자명과 암호를 확인하세요. " +
                           "SQL 인증을 쓰려면 서버가 혼합 모드(SQL Server 및 Windows 인증 모드)여야 합니다.";
                }
                return $"'{serverName}.{databaseName}'에 접속하지 못했습니다: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"'{serverName}.{databaseName}'에 접속하지 못했습니다: {ex.Message}";
            }
        }

        // ---------- 상태 갱신 ----------

        /// <summary>
        /// DDL 로그와 Git 상태를 다시 읽어 캐시를 갱신한다.
        /// </summary>
        /// <returns>갱신에 성공하면 true. 매핑이 없거나 DB에 접근할 수 없으면 false.</returns>
        public bool RefreshState(string serverName, string databaseName)
        {
            var mapping = _configManager.TryGetMapping(serverName, databaseName);
            if (mapping == null)
            {
                Debug.WriteLine($"'{serverName}.{databaseName}'에 매핑된 Git 저장소가 없습니다.");
                return false;
            }

            List<ChangeLogRow> rows;
            try
            {
                rows = ReadPendingRows(BuildConnectionString(serverName, databaseName));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StateTracker.RefreshState failed for '{serverName}.{databaseName}': {ex.Message}");
                return false;
            }

            var gitStates = _gitManager.GetChangedFileStates(mapping.GitPath);
            ApplyChangeSet(serverName, databaseName, BuildChangeSet(rows, gitStates));
            return true;
        }

        /// <summary>
        /// 아직 처리되지 않은 DDL 로그가 가리키는 객체의 스키마 한정 이름을 반환한다.
        /// 새로고침이 DB 전체가 아니라 바뀐 객체만 추출하도록 하는 대상 목록이다.
        ///
        /// 접속하지 못하면 빈 목록을 반환한다. 예외로 새로고침을 통째로 무너뜨리지 않기 위해서인데,
        /// 그러면 "바뀐 것이 없다"와 구분되지 않는다 — 호출자는 이 목록이 비어 있다는 것만으로
        /// 전체 추출을 건너뛰어서는 안 된다.
        /// </summary>
        public IReadOnlyList<string> GetChangedObjectNames(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return new List<string>();
            }

            try
            {
                return ToQualifiedNames(ReadPendingRows(BuildConnectionString(serverName, databaseName)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StateTracker.GetChangedObjectNames failed for '{serverName}.{databaseName}': {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 인덱스 이벤트를 부모 객체의 변경으로 바꾼다. 로그를 읽는 입구에서 한 번만 부른다 —
        /// 추출 대상(<see cref="GetChangedObjectNames"/>)과 화면 목록(<see cref="BuildChangeSet"/>)이
        /// 각자 해석하면 추출은 테이블을 뽑았는데 목록은 인덱스를 보여주는 식으로 갈라진다.
        ///
        /// 이벤트 타입도 함께 옮기는 것이 핵심이다. DROP_INDEX를 그대로 두면 상태가 Deleted가 되고
        /// WorkingTreeCleaner가 테이블의 .sql을 지운다 - 인덱스 하나를 지웠을 뿐인데.
        /// 인덱스 변경은 부모 테이블의 수정이지 삭제가 아니다.
        ///
        /// 부모를 모르면(v1이 남긴 행) 손대지 않는다. 지어낼 근거가 없다.
        /// </summary>
        internal static ChangeLogRow NormalizeRow(ChangeLogRow row)
        {
            if (row == null) return row!;
            if (!string.Equals(row.ObjectType?.Trim(), "INDEX", StringComparison.OrdinalIgnoreCase)) return row;
            if (string.IsNullOrWhiteSpace(row.TargetObjectName)) return row;

            return new ChangeLogRow
            {
                Id = row.Id,
                SchemaName = row.SchemaName,
                ObjectName = row.TargetObjectName!.Trim(),
                ObjectType = string.IsNullOrWhiteSpace(row.TargetObjectType) ? "TABLE" : row.TargetObjectType!.Trim(),
                EventType = "ALTER_TABLE",
                TargetObjectName = row.TargetObjectName,
                TargetObjectType = row.TargetObjectType
            };
        }

        /// <summary>
        /// 로그 행을 추출 대상 이름으로 바꾼다. 같은 객체를 여러 번 고쳤으면 행도 여러 개지만
        /// 추출은 한 번이면 되므로 중복을 없앤다.
        /// </summary>
        internal static IReadOnlyList<string> ToQualifiedNames(IEnumerable<ChangeLogRow> rows)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();

            foreach (var row in rows ?? Enumerable.Empty<ChangeLogRow>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.ObjectName)) continue;

                var qualifiedName = ObjectPathConvention.GetQualifiedName(row.SchemaName, row.ObjectName);
                if (seen.Add(qualifiedName))
                {
                    names.Add(qualifiedName);
                }
            }

            return names;
        }

        private static List<ChangeLogRow> ReadPendingRows(string connectionString)
        {
            var rows = new List<ChangeLogRow>();

            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = PendingChangesQuery;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(NormalizeRow(new ChangeLogRow
                {
                    Id = reader.GetInt32(0),
                    SchemaName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    ObjectName = reader.GetString(2),
                    ObjectType = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    EventType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    TargetObjectName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    TargetObjectType = reader.IsDBNull(6) ? null : reader.GetString(6)
                }));
            }

            return rows;
        }

        /// <summary>
        /// DDL 로그 행과 Git 작업 트리 상태를 종합해 객체별 최종 변경 목록을 만든다.
        /// 로그 행은 최신순으로 정렬되어 있다고 가정한다.
        /// </summary>
        internal IReadOnlyList<ChangeRecord> BuildChangeSet(
            IEnumerable<ChangeLogRow> rows,
            IReadOnlyDictionary<string, string>? gitStates)
        {
            var byPath = new Dictionary<string, ChangeRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows ?? Enumerable.Empty<ChangeLogRow>())
            {
                if (string.IsNullOrWhiteSpace(row.ObjectName)) continue;

                var relativePath = ObjectPathConvention.GetRelativePath(row.SchemaName, row.ObjectType, row.ObjectName);

                // 최신 이벤트가 먼저 오므로 처음 본 것만 채택한다.
                if (byPath.ContainsKey(relativePath)) continue;

                string? gitState = null;
                gitStates?.TryGetValue(relativePath, out gitState);

                byPath[relativePath] = new ChangeRecord
                {
                    Schema = string.IsNullOrWhiteSpace(row.SchemaName) ? ObjectPathConvention.DefaultSchema : row.SchemaName,
                    ObjectName = row.ObjectName,
                    ObjectType = row.ObjectType,
                    State = ResolveState(row.EventType, gitState),
                    QualifiedName = ObjectPathConvention.GetQualifiedName(row.SchemaName, row.ObjectName),
                    RelativePath = relativePath,
                    LastLogId = row.Id
                };
            }

            // DDL 로그에 없지만 Git에서 변경된 파일도 포함한다(트리거 설치 이전의 변경 등).
            if (gitStates != null)
            {
                foreach (var pair in gitStates)
                {
                    if (byPath.ContainsKey(pair.Key)) continue;
                    if (!ObjectPathConvention.TryParseRelativePath(pair.Key, out var schema, out var objectType, out var objectName)) continue;

                    byPath[pair.Key] = new ChangeRecord
                    {
                        Schema = schema,
                        ObjectName = objectName,
                        ObjectType = objectType,
                        State = pair.Value,
                        QualifiedName = ObjectPathConvention.GetQualifiedName(schema, objectName),
                        RelativePath = pair.Key,
                        LastLogId = 0
                    };
                }
            }

            return byPath.Values
                .OrderBy(r => r.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// DDL 이벤트와 Git 작업 트리 상태를 합쳐 추가/수정/삭제를 정한다.
        ///
        /// 추가냐 수정이냐는 저장소가 답을 갖고 있으므로 Git을 먼저 믿는다. DDL 로그의 최신
        /// 이벤트만 보면 틀린다 — SSMS 테이블 디자이너는 저장 한 번에 CREATE_TABLE 뒤로
        /// ALTER_TABLE을 더 흘려서, 방금 만든 테이블이 "수정"으로 뜬다. 반대로 아직 커밋된 적
        /// 없는 객체를 ALTER만 해도 파일은 신규인데 "수정"이 된다.
        ///
        /// DROP만 예외다. 파일 정리(WorkingTreeCleaner)는 이 판정 뒤에 돌기 때문에 Git은 아직
        /// 삭제를 보지 못한다. 그래서 삭제는 DDL 로그가 유일한 근거다.
        ///
        /// Git이 그 파일을 아무것도 보고하지 않으면(스크립트가 저장소의 것과 동일한 경우 등)
        /// 근거가 로그밖에 없으므로 이벤트 타입을 그대로 쓴다.
        /// </summary>
        internal static string ResolveState(string? eventType, string? gitState)
        {
            var ddlState = MapEventTypeToState(eventType);
            if (ddlState == "Deleted") return ddlState;

            return gitState == "Added" || gitState == "Modified" ? gitState! : ddlState;
        }

        /// <summary>
        /// DDL 이벤트 타입을 UI가 사용하는 상태 문자열로 변환한다.
        /// </summary>
        internal static string MapEventTypeToState(string? eventType)
        {
            if (string.IsNullOrWhiteSpace(eventType)) return "Modified";

            var normalized = eventType!.Trim();
            if (normalized.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)) return "Added";
            if (normalized.StartsWith("DROP", StringComparison.OrdinalIgnoreCase)) return "Deleted";
            return "Modified";
        }

        /// <summary>
        /// 해당 서버/DB의 변경 목록을 통째로 교체한다.
        /// 이전 새로고침의 잔여 항목이 남지 않도록 병합이 아니라 교체한다.
        /// </summary>
        internal void ApplyChangeSet(string serverName, string databaseName, IReadOnlyList<ChangeRecord> records)
        {
            _changesByDatabase[GetDatabaseKey(serverName, databaseName)] = records ?? new List<ChangeRecord>();
        }

        // ---------- 조회 ----------

        public IReadOnlyList<ChangeRecord> GetPendingChanges(string serverName, string databaseName)
        {
            return _changesByDatabase.TryGetValue(GetDatabaseKey(serverName, databaseName), out var records)
                ? records
                : new List<ChangeRecord>();
        }

        /// <summary>
        /// 객체의 최종 상태를 반환한다. 변경이 없으면 <c>"Clean"</c>.
        /// </summary>
        /// <param name="objectName"><c>dbo.Users</c> 형태의 스키마 한정 이름.</param>
        public string GetObjectState(string serverName, string databaseName, string objectName)
        {
            var record = GetPendingChanges(serverName, databaseName)
                .FirstOrDefault(r => string.Equals(r.QualifiedName, objectName, StringComparison.OrdinalIgnoreCase));

            return record?.State ?? "Clean";
        }

        // ---------- 동기화 표시 ----------

        /// <summary>
        /// 커밋된 객체의 DDL 로그 행을 처리 완료로 표시해 다음 새로고침에서 제외한다.
        /// 새로고침 시점 이후에 추가된 이벤트는 건드리지 않는다.
        /// </summary>
        public void MarkProcessed(string serverName, string databaseName, IEnumerable<ChangeRecord> records)
        {
            var targets = records?.Where(r => r.LastLogId > 0).ToList();
            if (targets == null || targets.Count == 0) return;

            try
            {
                using var conn = new SqlConnection(BuildConnectionString(serverName, databaseName));
                conn.Open();

                foreach (var record in targets)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = MarkProcessedCommand;
                    cmd.Parameters.AddWithValue("@lastLogId", record.LastLogId);
                    cmd.Parameters.AddWithValue("@objectName", record.ObjectName);
                    cmd.Parameters.AddWithValue("@schemaName", record.Schema ?? ObjectPathConvention.DefaultSchema);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StateTracker.MarkProcessed failed for '{serverName}.{databaseName}': {ex.Message}");
            }
        }

        private static string GetDatabaseKey(string serverName, string databaseName)
        {
            return $"{serverName}::{databaseName}";
        }
    }
}
