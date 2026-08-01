using System;
using System.Collections.Generic;

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

        private static string NormalizeSchema(string? schema)
        {
            return string.IsNullOrWhiteSpace(schema) ? DefaultSchema : schema!.Trim();
        }
    }
}
