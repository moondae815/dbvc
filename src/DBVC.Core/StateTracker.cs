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
        public const int RequiredSchemaVersion = 3;

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
        /// 아직 처리(커밋)되지 않은 DDL 이벤트만 최신순으로 읽는다. 전체 보기용이다.
        /// </summary>
        internal const string PendingChangesQuery = @"
SELECT Id, SchemaName, ObjectName, ObjectType, EventType, TargetObjectName, TargetObjectType, LoginName, HostName
FROM dbo.DBVC_ChangeLog
WHERE IsProcessed = 0
ORDER BY PostTime DESC, Id DESC";

        /// <summary>
        /// 지금 이 접속의 작업자가 낸 이벤트만 읽는다. 기본 화면이 쓰는 쿼리다.
        ///
        /// 두 쿼리를 문자열 결합 대신 따로 두는 이유는 읽기와 테스트 때문이다 -
        /// WHERE를 조립하면 어느 조합이 실제로 나가는지 눈으로 확인할 수 없다.
        /// </summary>
        internal const string PendingChangesByAuthorQuery = @"
SELECT Id, SchemaName, ObjectName, ObjectType, EventType, TargetObjectName, TargetObjectType, LoginName, HostName
FROM dbo.DBVC_ChangeLog
WHERE IsProcessed = 0
  AND ISNULL(LoginName, N'') = ISNULL(@login, N'')
  AND ISNULL(HostName, N'') = ISNULL(@host, N'')
ORDER BY PostTime DESC, Id DESC";

        /// <summary>
        /// "나는 누구인가"를 서버에게 묻는다. 클라이언트에서 Environment.MachineName으로 유도하면
        /// 접속 문자열의 Workstation ID를 누가 바꿔 두었을 때 트리거가 기록한 값과 달라지고,
        /// 필터가 전부를 걸러내 목록이 항상 빈다.
        /// </summary>
        internal const string CurrentAuthorQuery = "SELECT SUSER_SNAME(), HOST_NAME()";

        /// <summary>
        /// <see cref="NormalizeRow"/>가 부모 객체의 수정으로 바꿔치우는 자식 이벤트 타입.
        /// <see cref="MarkProcessedCommand"/>의 조건도 이 목록으로 만든다 — 두 곳이 갈라지면
        /// 커밋한 적 없는 행이 닫히거나(넓으면) 닫혀야 할 행이 남는다(좁으면).
        /// </summary>
        private static readonly string[] ParentPointingObjectTypes = { "INDEX", "COLUMN" };

        private static readonly string ParentPointingTypeList =
            string.Join(", ", ParentPointingObjectTypes.Select(t => $"N'{t}'"));

        /// <summary>
        /// 커밋된 객체의 로그 행을 닫는다. TargetObjectName도 보는 이유는 정규화 때문이다 -
        /// 레코드의 이름은 부모 테이블인데 인덱스 행의 ObjectName은 인덱스 이름이라,
        /// ObjectName만 보면 그 행이 영원히 열린 채로 남아 매번 다시 올라온다.
        ///
        /// 다만 <b>정규화되는 타입으로 좁힌다.</b> TargetObjectName은 인덱스 전용이 아니어서
        /// DML 트리거 이벤트도 부모 테이블을 거기 남긴다 - 타입을 보지 않으면 테이블만 커밋했는데
        /// 그 테이블에 딸린 트리거의 로그 행까지 닫혀, 커밋된 적 없는 변경이 조용히 사라진다.
        /// </summary>
        internal static readonly string MarkProcessedCommand = $@"
UPDATE dbo.DBVC_ChangeLog
SET IsProcessed = 1
WHERE IsProcessed = 0 AND Id <= @lastLogId
  AND (ObjectName = @objectName
       OR (ObjectType IN ({ParentPointingTypeList}) AND TargetObjectName = @objectName))
  AND (ISNULL(SchemaName, N'dbo') = @schemaName)
  AND ISNULL(LoginName, N'') = ISNULL(@login, N'')
  AND ISNULL(HostName, N'') = ISNULL(@host, N'')";

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
            => RefreshState(serverName, databaseName, includeAllAuthors: false);

        /// <summary>
        /// DDL 로그와 Git 상태를 다시 읽어 캐시를 갱신한다.
        /// </summary>
        /// <param name="includeAllAuthors">
        /// true면 다른 사람이 만든 변경까지 읽는다. 기본 화면은 false다 —
        /// 공용 계정 환경에서 필터가 없으면 목록에 남의 진행 중 작업이 전부 뜨고,
        /// 전체 선택 커밋 한 번이면 검증되지 않은 남의 작업이 브랜치에 담긴다.
        /// </param>
        /// <returns>갱신에 성공하면 true. 매핑이 없거나 DB에 접근할 수 없으면 false.</returns>
        public bool RefreshState(string serverName, string databaseName, bool includeAllAuthors)
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
                var connectionString = BuildConnectionString(serverName, databaseName);
                rows = ReadPendingRows(
                    connectionString,
                    includeAllAuthors ? null : (ValueTuple<string?, string?>?)ReadCurrentAuthor(connectionString));
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
                // 추출 대상은 작업자로 좁히지 않는다. 파일을 쓰는 것과 커밋에 담는 것은 다른 문제이고,
                // 남의 변경을 추출에서 빼면 그 객체의 파일이 낡은 채로 남아 다음 커밋에 섞여 들어간다.
                return ToQualifiedNames(ReadPendingRows(BuildConnectionString(serverName, databaseName), null));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StateTracker.GetChangedObjectNames failed for '{serverName}.{databaseName}': {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 인덱스·컬럼 이벤트를 부모 객체의 변경으로 바꾼다. 로그를 읽는 입구에서 한 번만 부른다 —
        /// 추출 대상(<see cref="GetChangedObjectNames"/>)과 화면 목록(<see cref="BuildChangeSet"/>)이
        /// 각자 해석하면 추출은 테이블을 뽑았는데 목록은 인덱스를 보여주는 식으로 갈라진다.
        ///
        /// 둘 다 독립 파일이 되지 않고 부모의 스크립트 안에 담긴다. 특히 컬럼 이름 변경(sp_rename)은
        /// COLUMN 이벤트 하나만 남기고 테이블 이벤트를 따로 내지 않아, 옮기지 않으면 그 변경이
        /// 저장소에 영영 반영되지 않는다.
        ///
        /// 이벤트 타입도 함께 옮기는 것이 핵심이다. DROP_INDEX를 그대로 두면 상태가 Deleted가 되고
        /// WorkingTreeCleaner가 테이블의 .sql을 지운다 - 인덱스 하나를 지웠을 뿐인데.
        /// 자식 객체의 변경은 부모의 수정이지 삭제가 아니다.
        ///
        /// 부모를 모르면(v1이 남긴 행) 손대지 않는다. 지어낼 근거가 없다.
        /// </summary>
        internal static ChangeLogRow NormalizeRow(ChangeLogRow row)
        {
            if (row == null) return row!;
            var objectType = row.ObjectType?.Trim();
            if (!ParentPointingObjectTypes.Any(t => string.Equals(objectType, t, StringComparison.OrdinalIgnoreCase))) return row;
            if (string.IsNullOrWhiteSpace(row.TargetObjectName)) return row;

            return new ChangeLogRow
            {
                Id = row.Id,
                SchemaName = row.SchemaName,
                ObjectName = row.TargetObjectName!.Trim(),
                // 부모 타입을 그대로 옮긴다. 인덱싱된 뷰의 인덱스는 여기가 VIEW로 오므로
                // TABLE로 못박으면 그 뷰를 dbo/Tables/... 로 보내 실제 파일과 다른 경로를 얻는다.
                // 그럼에도 TABLE을 최후 수단으로 두는 이유는, 부모가 있는데 타입만 비어 있으면
                // Other 폴더로 떨어져 영원히 커밋되지 않기 때문이다 - 자식을 갖는 객체는
                // 압도적으로 테이블이라 틀릴 확률이 가장 낮은 추측이다.
                ObjectType = string.IsNullOrWhiteSpace(row.TargetObjectType) ? "TABLE" : row.TargetObjectType!.Trim(),
                EventType = "ALTER_TABLE",
                TargetObjectName = row.TargetObjectName,
                TargetObjectType = row.TargetObjectType,
                LoginName = row.LoginName,
                HostName = row.HostName
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

        /// <param name="author">null이면 전체를 읽는다(전체 보기).</param>
        private static List<ChangeLogRow> ReadPendingRows(string connectionString, (string? Login, string? Host)? author)
        {
            var rows = new List<ChangeLogRow>();

            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();

            if (author == null)
            {
                cmd.CommandText = PendingChangesQuery;
            }
            else
            {
                cmd.CommandText = PendingChangesByAuthorQuery;
                cmd.Parameters.AddWithValue("@login", (object?)author.Value.Login ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@host", (object?)author.Value.Host ?? DBNull.Value);
            }

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
                    TargetObjectType = reader.IsDBNull(6) ? null : reader.GetString(6),
                    LoginName = reader.IsDBNull(7) ? null : reader.GetString(7),
                    HostName = reader.IsDBNull(8) ? null : reader.GetString(8)
                }));
            }

            return rows;
        }

        /// <summary>
        /// 지금 이 접속이 서버에서 어떻게 보이는지 읽는다. 트리거가 기록하는 값과 같은 함수를
        /// 같은 접속에서 부르므로 정의상 일치한다.
        /// </summary>
        private static (string? Login, string? Host) ReadCurrentAuthor(string connectionString)
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = CurrentAuthorQuery;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (null, null);

            return (reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1));
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
                    LastLogId = row.Id,
                    Author = row.LoginName,
                    HostName = row.HostName
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

        public IReadOnlyList<CoAuthorWarning> GetCoAuthorWarnings(
            string serverName, string databaseName, IEnumerable<string> qualifiedNames)
        {
            try
            {
                var connectionString = BuildConnectionString(serverName, databaseName);
                var current = ReadCurrentAuthor(connectionString);

                // 필터 없이 읽는다. "내 변경만" 상태에서도 남이 만졌다는 사실은 알려야 한다.
                var rows = ReadPendingRows(connectionString, author: null);

                return CoAuthorDetector.Detect(rows, qualifiedNames, current.Login, current.Host);
            }
            catch (Exception ex)
            {
                // 경고를 못 내는 것이 커밋을 막을 이유는 되지 않는다.
                Debug.WriteLine($"StateTracker.GetCoAuthorWarnings failed for '{serverName}.{databaseName}': {ex.Message}");
                return Array.Empty<CoAuthorWarning>();
            }
        }

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
                    // 현재 사용자가 아니라 레코드의 작업자로 좁힌다. 전체 보기에서 남의 변경을
                    // 대신 커밋하는 경로가 있고, 현재 사용자로 좁히면 그 행이 영원히 닫히지 않는다.
                    cmd.Parameters.AddWithValue("@login", (object?)record.Author ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@host", (object?)record.HostName ?? DBNull.Value);
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
