using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DBVC.Vsix.Services;
using ICSharpCode.AvalonEdit.Rendering;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// Diff 줄의 배경을 칠한다. 화면에 보이는 줄만 그린다.
    /// </summary>
    public class DiffLineBackgroundRenderer : IBackgroundRenderer
    {
        private readonly TextView _textView;
        private IReadOnlyList<DiffLineKind> _lineKinds = new List<DiffLineKind>();

        public DiffLineBackgroundRenderer(TextView textView)
        {
            _textView = textView;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public Brush InsertedBrush { get; set; } = Frozen("#E6FFED");
        public Brush DeletedBrush { get; set; } = Frozen("#FFEEF0");
        public Brush ModifiedBrush { get; set; } = Frozen("#FFF5B1");
        public Brush PaddingBrush { get; set; } = Frozen("#F0F0F0");

        /// <summary>
        /// 줄 종류를 교체하고 배경을 다시 그리게 한다.
        /// 텍스트를 먼저 설정한 뒤 호출해야 이전 종류로 한 번 그리는 일이 없다.
        /// </summary>
        public void SetLineKinds(IReadOnlyList<DiffLineKind>? lineKinds)
        {
            _lineKinds = lineKinds ?? new List<DiffLineKind>();
            _textView.InvalidateLayer(Layer);
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_lineKinds.Count == 0) return;

            textView.EnsureVisualLines();

            foreach (var visualLine in textView.VisualLines)
            {
                var lineNumber = visualLine.FirstDocumentLine.LineNumber;

                // 텍스트와 종류 배열이 잠시 어긋난 순간에도 예외를 던지지 않는다.
                if (lineNumber < 1 || lineNumber > _lineKinds.Count) continue;

                var brush = BrushFor(_lineKinds[lineNumber - 1]);
                if (brush == null) continue;

                // 빈 줄도 칠해야 패딩이 보이므로 사각형 폭은 뷰 전체로 잡는다.
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, visualLine.FirstDocumentLine))
                {
                    drawingContext.DrawRectangle(brush, null,
                        new Rect(0, rect.Top, textView.ActualWidth, rect.Height));
                }
            }
        }

        private Brush? BrushFor(DiffLineKind kind)
        {
            switch (kind)
            {
                case DiffLineKind.Inserted: return InsertedBrush;
                case DiffLineKind.Deleted: return DeletedBrush;
                case DiffLineKind.Modified: return ModifiedBrush;
                case DiffLineKind.Padding: return PaddingBrush;
                default: return null;
            }
        }

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

            // 고정 색이므로 얼려 두면 렌더링마다 재검증하지 않는다.
            brush.Freeze();
            return brush;
        }
    }
}
