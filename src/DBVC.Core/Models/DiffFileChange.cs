namespace DBVC.Core.Models
{
    /// <summary>
    /// 파일 하나의 변경. <see cref="Patch"/>는 그 파일에 해당하는 unified diff 본문이다.
    ///
    /// 파일별로 나눠 두는 이유는 잘라내기다 — 한 덩어리 문자열이면 앞쪽 객체가 뒤쪽 객체의
    /// 몫까지 먹어 마지막 객체의 변경이 통째로 사라진다.
    /// </summary>
    public class DiffFileChange
    {
        public string RelativePath { get; set; } = string.Empty;

        /// <summary><c>Added</c> · <c>Modified</c> · <c>Deleted</c>. 화면 계층이 쓰는 값과 같은 영어 식별자다.</summary>
        public string Status { get; set; } = string.Empty;

        public string Patch { get; set; } = string.Empty;
    }
}
