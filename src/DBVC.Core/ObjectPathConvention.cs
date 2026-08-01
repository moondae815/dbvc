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

        private static readonly Dictionary<string, string> FolderByObjectType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // SMO 타입명
            ["Table"] = "Tables",
            ["View"] = "Views",
            ["StoredProcedure"] = "StoredProcedures",
            ["UserDefinedFunction"] = "Functions",
            ["Trigger"] = "Triggers",
            ["UserDefinedType"] = "Types",
            ["UserDefinedDataType"] = "Types",
            ["UserDefinedTableType"] = "TableTypes",
            ["Sequence"] = "Sequences",
            ["Synonym"] = "Synonyms",

            // DDL 트리거 EVENTDATA의 ObjectType 값
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

        public static string GetFolderName(string? objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return UnknownFolder;
            return FolderByObjectType.TryGetValue(objectType!.Trim(), out var folder) ? folder : UnknownFolder;
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
    }
}
