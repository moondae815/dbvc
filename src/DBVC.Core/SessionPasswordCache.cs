using System;
using System.Collections.Concurrent;

namespace DBVC.Core
{
    /// <summary>
    /// 이 프로세스에서만 유효한 (서버, 데이터베이스)별 평문 암호.
    ///
    /// SSMS 개체 탐색기에서 가져온 암호가 여기에 들어간다. 사용자가 직접 입력한 암호와 달리
    /// 디스크에 남기지 않기로 한 값이므로, 파일을 다루는 <see cref="SqlCredentialStore"/>와
    /// 수명이 다르다. 두 수명을 한 클래스에 섞으면 직렬화 코드가 "이 항목은 쓰면 안 된다"는
    /// 분기를 들고 다니게 되므로 분리한다.
    ///
    /// 키 규약은 <see cref="SqlCredentialStore"/>와 같아야 한다 — 같은 (서버, DB)를 서로 다른
    /// 항목으로 보면 세션 암호가 조회되지 않는다.
    /// </summary>
    public class SessionPasswordCache
    {
        private readonly ConcurrentDictionary<string, string> _passwords =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 세션 암호를 기록한다. <c>null</c>이거나 빈 문자열이면 기존 항목을 제거한다 —
        /// "암호 없음"을 빈 문자열로 들고 있으면 조회 쪽에서 다시 분기해야 한다.
        /// </summary>
        public void Set(string serverName, string databaseName, string? plainPassword)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return;
            }

            if (string.IsNullOrEmpty(plainPassword))
            {
                Remove(serverName, databaseName);
                return;
            }

            _passwords[GetKey(serverName, databaseName)] = plainPassword!;
        }

        public string? TryGet(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return null;
            }

            return _passwords.TryGetValue(GetKey(serverName, databaseName), out var password)
                ? password
                : null;
        }

        public bool Remove(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return false;
            }

            return _passwords.TryRemove(GetKey(serverName, databaseName), out _);
        }

        private static string GetKey(string serverName, string databaseName)
        {
            return $"{serverName}::{databaseName}";
        }
    }
}
