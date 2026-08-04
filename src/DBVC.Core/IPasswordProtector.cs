namespace DBVC.Core
{
    /// <summary>
    /// 암호를 디스크에 남길 수 있는 형태로 보호하고 되돌린다.
    ///
    /// DPAPI는 Windows 전용인데 <c>DBVC.Core</c>는 netstandard2.0으로도 빌드되어
    /// macOS/Linux CI에서 컴파일된다. 그 경계를 이 인터페이스로 끊어 두면
    /// 비Windows에서도 빌드·테스트가 성립한다.
    /// </summary>
    public interface IPasswordProtector
    {
        /// <summary>현재 플랫폼에서 보호가 가능한지. false면 암호를 저장할 수 없다.</summary>
        bool IsSupported { get; }

        /// <summary>
        /// 평문 암호를 보호된 문자열로 바꾼다. 실패하거나 지원되지 않으면 <c>null</c>.
        /// </summary>
        /// <param name="purpose">
        /// 항목마다 다른 값을 주면 보호된 값이 다른 항목으로 옮겨져도 풀리지 않는다.
        /// </param>
        string? Protect(string? plainText, string purpose);

        /// <summary>
        /// <see cref="Protect"/>의 역연산. 다른 Windows 계정이거나 값이 손상됐으면 <c>null</c>.
        /// </summary>
        string? Unprotect(string? protectedText, string purpose);
    }
}
