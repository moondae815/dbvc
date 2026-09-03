using System;
using System.IO;
using System.Text;
using LibGit2Sharp;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// DBVC는 저장소의 .sql을 여섯 자리에서 읽는데 어디에서도 인코딩을 지정하지 않는다.
    /// 저장소 인코딩을 UTF-8로 바꾸면서 그 여섯 곳을 손대지 않기로 한 근거가 여기 고정되어 있다 —
    /// File.ReadAllText와 Blob.GetContentText가 둘 다 BOM을 감지하고, 없으면 UTF-8로 읽는다.
    ///
    /// 이 전제가 깨지면(라이브러리 업그레이드 등) Diff의 Old쪽이 조용히 깨진다. 화면에는 깨진
    /// 글자가 아니라 "전부 변경됨"으로 나타나 원인을 짐작하기 어렵다 — Old는 블롭에서,
    /// New는 작업 트리에서 오기 때문이다.
    ///
    /// UTF-16 항목을 남겨 두는 이유는 전환 이전에 만들어진 커밋의 블롭이 영원히 UTF-16이기
    /// 때문이다. 옛 이력의 Diff는 앞으로도 이 경로로 읽힌다.
    /// </summary>
    [TestFixture]
    public class FileEncodingTests
    {
        private static readonly Signature TestSignature =
            new Signature("Test", "test@example.com", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        /// <summary>한국어를 넣는다. ASCII만으로는 인코딩이 어긋나도 통과해 버린다.</summary>
        private const string Sql = "CREATE PROCEDURE dbo.P AS SELECT 1 -- 한글 주석";

        private static Encoding EncodingFor(string kind)
        {
            switch (kind)
            {
                case "utf16": return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                case "utf8bom": return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                default: return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
        }

        private static string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_enc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        [TestCase("utf16")]
        [TestCase("utf8bom")]
        [TestCase("utf8nobom")]
        public void FileReadAllText_RoundTripsTheText_ForEveryEncodingDbvcMayEncounter(string kind)
        {
            var file = Path.Combine(NewTempDir(), "p.sql");
            File.WriteAllText(file, Sql, EncodingFor(kind));

            Assert.That(File.ReadAllText(file), Is.EqualTo(Sql));
        }

        [TestCase("utf16")]
        [TestCase("utf8bom")]
        [TestCase("utf8nobom")]
        public void BlobGetContentText_RoundTripsTheText_ForEveryEncodingDbvcMayEncounter(string kind)
        {
            var dir = NewTempDir();
            File.WriteAllText(Path.Combine(dir, "p.sql"), Sql, EncodingFor(kind));

            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commands.Stage(repo, "*");
            var commit = repo.Commit("initial", TestSignature, TestSignature);

            var blob = (Blob)commit["p.sql"].Target;
            Assert.That(blob.GetContentText(), Is.EqualTo(Sql));
        }
    }
}
