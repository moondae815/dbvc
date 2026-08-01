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
                default: return DiffLineKind.Unchanged;
            }
        }
    }
}
