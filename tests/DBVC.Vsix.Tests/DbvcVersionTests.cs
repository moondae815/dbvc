using System;
using System.IO;
using System.Xml.Linq;
using NUnit.Framework;
using DBVC.Vsix;

namespace DBVC.Vsix.Tests
{
    [TestFixture]
    public class DbvcVersionTests
    {
        [Test]
        public void Format_StripsBuildMetadata_WhenInformationalVersionHasCommitHash()
        {
            Assert.That(DbvcVersion.Format("0.2.7+9f3a1c2"), Is.EqualTo("0.2.7"));
        }

        [Test]
        public void Format_ReturnsValueUnchanged_WhenNoBuildMetadata()
        {
            Assert.That(DbvcVersion.Format("0.2.7"), Is.EqualTo("0.2.7"));
        }

        [Test]
        public void Format_ReturnsUnknownLabel_WhenValueIsMissing()
        {
            Assert.That(DbvcVersion.Format(null), Is.EqualTo("알 수 없음"));
            Assert.That(DbvcVersion.Format("   "), Is.EqualTo("알 수 없음"));
        }

        /// <summary>
        /// 이 테스트가 지키는 것은 문자열이 아니라 배선이다. csproj의 XmlPeek 타깃이 조용히
        /// 끊기면 InformationalVersion이 기본값(1.0.0)으로 돌아가고, 화면에는 아무 오류 없이
        /// 틀린 버전이 뜬다. 매니페스트와 직접 맞대어 그 경우를 여기서 붙잡는다.
        /// </summary>
        [Test]
        public void Current_MatchesVsixManifest()
        {
            var manifestPath = Path.Combine(
                TestContext.CurrentContext.TestDirectory, "source.extension.vsixmanifest");
            Assert.That(File.Exists(manifestPath), Is.True,
                "매니페스트가 테스트 출력에 복사되지 않았다. csproj의 링크 항목을 확인한다.");

            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/vsx-schema/2011");
            var expected = XDocument.Load(manifestPath)
                .Element(ns + "PackageManifest")!
                .Element(ns + "Metadata")!
                .Element(ns + "Identity")!
                .Attribute("Version")!.Value;

            Assert.That(DbvcVersion.Current, Is.EqualTo(expected));
        }
    }
}
