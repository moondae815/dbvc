using System.Collections.Generic;
using System.Text;
using DiffPlex.DiffBuilder.Model;

namespace DBVC.Vsix.Services
{
    /// <summary>Diff 한 줄의 종류. 배경색 결정에 쓴다.</summary>
    public enum DiffLineKind
    {
        Unchanged,
        Inserted,
        Deleted,
        Modified,

        /// <summary>반대편에만 줄이 있어 좌우를 맞추려고 넣은 빈 줄.</summary>
        Padding
    }

    /// <summary>에디터 한쪽에 넣을 텍스트와 줄별 종류.</summary>
    public class DiffPane
    {
        public string Text { get; set; } = string.Empty;

        /// <summary>1-based 줄 번호에 대응한다. 인덱스 0이 문서의 1번 줄이다.</summary>
        public IReadOnlyList<DiffLineKind> LineKinds { get; set; } = new List<DiffLineKind>();
    }

    /// <summary>
    /// DiffPlex의 한쪽 결과를 AvalonEdit에 넣을 텍스트와 줄 종류로 바꾼다.
    /// WPF·파일 시스템에 의존하지 않는 순수 변환이다.
    /// </summary>
    public static class DiffTextBuilder
    {
        public static DiffPane Build(IEnumerable<DiffPiece>? lines)
        {
            var kinds = new List<DiffLineKind>();
            var builder = new StringBuilder();
            var isFirst = true;

            foreach (var line in lines ?? new List<DiffPiece>())
            {
                if (line == null) continue;

                if (!isFirst) builder.Append('\n');
                isFirst = false;

                // Imaginary 줄은 Text가 null이다. 빈 줄로 만들어 좌우 정렬을 맞춘다.
                builder.Append(line.Text ?? string.Empty);
                kinds.Add(MapChangeType(line.Type));
            }

            // 빈 문서도 실제로는 0줄이 아니라 1줄(내용 없는 한 줄)이다.
            // Text.Split('\n')과 AvalonEdit의 TextDocument도 그렇게 취급하므로,
            // 줄 번호로 색을 찾는 렌더러가 어긋나지 않도록 kinds를 비워 두지 않는다.
            if (kinds.Count == 0) kinds.Add(DiffLineKind.Unchanged);

            return new DiffPane { Text = builder.ToString(), LineKinds = kinds };
        }

        private static DiffLineKind MapChangeType(ChangeType type)
        {
            switch (type)
            {
                case ChangeType.Inserted: return DiffLineKind.Inserted;
                case ChangeType.Deleted: return DiffLineKind.Deleted;
                case ChangeType.Modified: return DiffLineKind.Modified;
                case ChangeType.Imaginary: return DiffLineKind.Padding;
                // 의도적인 선택이다: 향후 DiffPlex가 새 ChangeType을 추가해도 예외를 던지지 않고
                // 강조 없이 그대로 렌더링한다.
                default: return DiffLineKind.Unchanged;
            }
        }
    }
}
