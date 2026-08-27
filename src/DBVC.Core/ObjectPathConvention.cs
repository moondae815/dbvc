using System;
using System.Collections.Generic;
using System.Linq;

namespace DBVC.Core
{
    /// <summary>
    /// 설계 4.2의 저장 구조 규약 <c>[Schema]/[ObjectType]/[ObjectName].sql</c>을 한 곳에서 정의한다.
    /// SMO 타입명과 DDL 트리거가 기록하는 EVENTDATA의 ObjectType 값을 모두 같은 폴더로 매핑한다.
    /// </summary>
    public static class ObjectPathConvention
    {
        public const string DefaultSchema = "dbo";
        public const string UnknownFolder = "Other";

        /// <summary>SMO가 내놓는 타입명. 추출 경로가 쓴다.</summary>
        private static readonly Dictionary<string, string> SmoFolderByObjectType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Table"] = "Tables",
            ["View"] = "Views",
            ["StoredProcedure"] = "StoredProcedures",
            ["UserDefinedFunction"] = "Functions",
            ["Trigger"] = "Triggers",
            ["UserDefinedType"] = "Types",
            ["UserDefinedDataType"] = "Types",
            ["UserDefinedTableType"] = "TableTypes",
            ["Sequence"] = "Sequences",
            ["Synonym"] = "Synonyms"
        };

        /// <summary>
        /// DDL 트리거 EVENTDATA의 ObjectType 값. <b>설치 스크립트의 DBVC_TRACKED_TYPES와 같은 목록이어야
        /// 하며</b>, 어긋나면 InstallScriptSyncTests가 죽는다 — 트리거가 기록하지 않는 타입을 여기 두면
        /// 화면 코드가 영원히 오지 않는 값을 기다리고, 반대면 파일이 없는 항목이 목록에 뜬다.
        /// </summary>
        private static readonly Dictionary<string, string> DdlFolderByObjectType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TABLE"] = "Tables",
            ["VIEW"] = "Views",
            ["PROCEDURE"] = "StoredProcedures",
            ["SQL_STORED_PROCEDURE"] = "StoredProcedures",
            ["FUNCTION"] = "Functions",
            ["SQL_SCALAR_FUNCTION"] = "Functions",
            ["SQL_TABLE_VALUED_FUNCTION"] = "Functions",
            ["SQL_INLINE_TABLE_VALUED_FUNCTION"] = "Functions",
            ["TRIGGER"] = "Triggers",
            ["SQL_TRIGGER"] = "Triggers",
            ["TYPE"] = "Types",
            ["TABLE_TYPE"] = "TableTypes",
            ["SEQUENCE OBJECT"] = "Sequences",
            ["SEQUENCE_OBJECT"] = "Sequences",
            ["SEQUENCE"] = "Sequences",
            ["SYNONYM"] = "Synonyms"
        };

        /// <summary>설치 스크립트의 화이트리스트와 대조되는 목록. INDEX는 여기 없다 — 독립 객체로 저장되지 않고 부모 테이블로 정규화된다.</summary>
        internal static IReadOnlyCollection<string> DdlEventObjectTypes => DdlFolderByObjectType.Keys.ToList();

        public static string GetFolderName(string? objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return UnknownFolder;
            var key = objectType!.Trim();
            if (SmoFolderByObjectType.TryGetValue(key, out var smoFolder)) return smoFolder;
            return DdlFolderByObjectType.TryGetValue(key, out var ddlFolder) ? ddlFolder : UnknownFolder;
        }

        /// <summary>
        /// 저장소 상대 경로를 반환한다. 구분자는 Git 규약대로 슬래시('/')이다.
        /// </summary>
        public static string GetRelativePath(string? schema, string? objectType, string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
            {
                throw new ArgumentException("Object name cannot be null or whitespace.", nameof(objectName));
            }
            return $"{NormalizeSchema(schema)}/{GetFolderName(objectType)}/{objectName.Trim()}.sql";
        }

        public static string GetQualifiedName(string? schema, string objectName)
        {
            return $"{NormalizeSchema(schema)}.{objectName?.Trim()}";
        }

        private static readonly Dictionary<string, string> ObjectTypeByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tables"] = "Table",
            ["Views"] = "View",
            ["StoredProcedures"] = "StoredProcedure",
            ["Functions"] = "UserDefinedFunction",
            ["Triggers"] = "Trigger",
            ["Types"] = "UserDefinedType",
            ["TableTypes"] = "UserDefinedTableType",
            ["Sequences"] = "Sequence",
            ["Synonyms"] = "Synonym"
        };

        /// <summary>
        /// 배포 스크립트에서 객체 타입 그룹을 배치하는 관례적 순서. (script-generation 설계 3.3)
        /// 의존성 해석이 아니라 결정적 정렬을 위한 것이다.
        /// </summary>
        private static readonly Dictionary<string, int> SortOrderByObjectType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["UserDefinedType"] = 0,
            ["UserDefinedDataType"] = 0,
            ["UserDefinedTableType"] = 1,
            ["Table"] = 2,
            ["Sequence"] = 3,
            ["Synonym"] = 4,
            ["View"] = 5,
            ["UserDefinedFunction"] = 6,
            ["StoredProcedure"] = 7,
            ["Trigger"] = 8
        };

        public static int GetTypeSortOrder(string? objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return int.MaxValue;
            return SortOrderByObjectType.TryGetValue(objectType!.Trim(), out var order) ? order : int.MaxValue;
        }

        /// <summary>
        /// <c>dbo/Tables/Users.sql</c> 형태의 상대 경로를 스키마/객체 타입/객체명으로 되돌린다.
        /// 규약에 맞지 않는 경로면 false를 반환한다.
        /// </summary>
        public static bool TryParseRelativePath(string? relativePath, out string schema, out string objectType, out string objectName)
        {
            schema = DefaultSchema;
            objectType = UnknownFolder;
            objectName = string.Empty;

            if (string.IsNullOrWhiteSpace(relativePath)) return false;

            var normalized = relativePath!.Replace('\\', '/');
            if (!normalized.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) return false;

            var segments = normalized.Split('/');
            if (segments.Length != 3) return false;
            if (segments.Any(string.IsNullOrWhiteSpace)) return false;

            schema = segments[0];
            objectType = ObjectTypeByFolder.TryGetValue(segments[1], out var mapped) ? mapped : UnknownFolder;
            objectName = segments[2].Substring(0, segments[2].Length - ".sql".Length);
            return objectName.Length > 0;
        }

        private static string NormalizeSchema(string? schema)
        {
            return string.IsNullOrWhiteSpace(schema) ? DefaultSchema : schema!.Trim();
        }

        /// <summary>
        /// T-SQL의 <c>CREATE OR ALTER</c>가 이 타입을 받는가. 받는 것은 넷뿐이다 —
        /// 프로시저·뷰·함수·트리거.
        ///
        /// 저장소 파일은 <c>ScriptForCreateOrAlter</c>로 저장되어 있으므로, 이 넷은
        /// 대상에 있든 없든 그대로 실행된다. 나머지는 대상에 이미 있으면 실패하므로
        /// 배포 스크립트에서 빼야 한다. <b>테이블만 빼면 안 된다</b> — Sequence·Synonym·
        /// UserDefinedType도 같은 자리에 있다.
        ///
        /// 모르는 타입은 false다. 실행 실패보다 "손으로 하세요"가 낫다.
        /// </summary>
        private static readonly HashSet<string> CreateOrAlterTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "StoredProcedure",
            "View",
            "UserDefinedFunction",
            "Trigger"
        };

        public static bool SupportsCreateOrAlter(string? objectType)
        {
            return !string.IsNullOrWhiteSpace(objectType) && CreateOrAlterTypes.Contains(objectType!.Trim());
        }
    }
}
