using System;
using System.Collections.Generic;
using DBVC.Core.Models;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 차이 하나를 사용자에게 뭐라고 부를지 정한다. mode에 따라 다르다 —
    /// 운영(audit)에서는 트리거가 없어 "미배포"인지 "무단 변경"인지 구분할 수 없으므로
    /// 구분되는 척하지 않는다.
    /// </summary>
    public static class DifferenceTextProvider
    {
        public static string GetStateText(ObjectDiffState state, MappingMode mode)
        {
            if (mode == MappingMode.Audit) return "확인 필요";

            switch (state)
            {
                case ObjectDiffState.MissingInDatabase: return "배포 필요 (신규)";
                case ObjectDiffState.Modified: return "배포 필요 (내용 다름)";
                case ObjectDiffState.MissingInBranch: return "DB에만 있음";
                default: throw new InvalidOperationException($"처리되지 않은 {nameof(ObjectDiffState)}: {state}");
            }
        }

        private static readonly Dictionary<string, string> KoreanByObjectType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Table"] = "테이블",
            ["View"] = "뷰",
            ["StoredProcedure"] = "저장 프로시저",
            ["UserDefinedFunction"] = "함수",
            ["Trigger"] = "트리거",
            ["UserDefinedType"] = "형식",
            ["UserDefinedDataType"] = "형식",
            ["UserDefinedTableType"] = "테이블 형식",
            ["Sequence"] = "시퀀스",
            ["Synonym"] = "동의어"
        };

        /// <summary>모르는 타입은 원문 그대로 보여준다. 빈칸보다 낫다.</summary>
        public static string GetObjectTypeText(string? objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return string.Empty;
            return KoreanByObjectType.TryGetValue(objectType!.Trim(), out var korean) ? korean : objectType!.Trim();
        }
    }

    public class DifferenceItemViewModel
    {
        public DifferenceItemViewModel(SchemaDifference difference, MappingMode mode)
        {
            Difference = difference ?? throw new ArgumentNullException(nameof(difference));
            StateText = DifferenceTextProvider.GetStateText(difference.State, mode);
            ObjectTypeText = DifferenceTextProvider.GetObjectTypeText(difference.ObjectType);
        }

        public SchemaDifference Difference { get; }
        public string QualifiedName => Difference.QualifiedName;
        public string RelativePath => Difference.RelativePath;
        public string ObjectTypeText { get; }
        public string StateText { get; }
    }
}
