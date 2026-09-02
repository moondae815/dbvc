using System;
using DBVC.Core;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 이력 뷰에서 커밋 선택 시 표시되는 변경된 파일 목록의 한 행.
    /// Core의 <see cref="HistoryChangedFile"/>을 WPF 바인딩에 맞게 변환한다.
    /// </summary>
    public class HistoryChangedFileViewModel
    {
        public HistoryChangedFileState State { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public string ObjectType { get; set; } = string.Empty;

        public string ObjectTypeText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ObjectType))
                    return string.Empty;

                var upperType = ObjectType.Trim().ToUpperInvariant();
                return upperType switch
                {
                    "PROCEDURE" or "STOREDPROCEDURE" => "SP",
                    "FUNCTION" or "USERDEFINEDFUNCTION" => "UDF",
                    "TABLE" => "Table",
                    "VIEW" => "View",
                    "TRIGGER" => "Trigger",
                    _ => char.ToUpper(upperType[0]) + upperType.Substring(1).ToLowerInvariant()
                };
            }
        }

        /// <summary>
        /// 화면에 표시할 한국어 상태 텍스트.
        /// </summary>
        public string StateText => State switch
        {
            HistoryChangedFileState.Added => "추가",
            HistoryChangedFileState.Modified => "수정",
            HistoryChangedFileState.Deleted => "삭제",
            _ => State.ToString()
        };

        public static HistoryChangedFileViewModel From(HistoryChangedFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));

            var vm = new HistoryChangedFileViewModel
            {
                State = file.State,
                RelativePath = file.RelativePath ?? string.Empty
            };

            if (ObjectPathConvention.TryParseRelativePath(file.RelativePath, out var schema, out var objectType, out var objectName))
            {
                vm.ObjectName = ObjectPathConvention.GetQualifiedName(schema, objectName);
                vm.ObjectType = objectType;
            }
            else
            {
                vm.ObjectName = file.RelativePath ?? string.Empty;
                vm.ObjectType = string.Empty;
            }

            return vm;
        }
    }
}
