using DBVC.Baseline;

namespace DBVC.Baseline.Tests
{
    public class TempConfigTests
    {
        [Test]
        public void CreateDirectory_ReturnsPathUnderTempPath()
        {
            var path = TempConfig.CreateDirectory();
            try
            {
                Assert.That(path, Does.StartWith(Path.GetTempPath()));
                Assert.That(Directory.Exists(path), Is.True);
            }
            finally
            {
                Directory.Delete(path, recursive: true);
            }
        }

        [Test]
        public void CreateDirectory_ReturnsPathOutsideAppData()
        {
            // 사용자의 진짜 mappings.json을 건드리면 운영이 "개발"로 매핑된 상태가 남는다.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var path = TempConfig.CreateDirectory();
            try
            {
                Assert.That(path, Does.Not.StartWith(appData));
            }
            finally
            {
                Directory.Delete(path, recursive: true);
            }
        }

        [Test]
        public void CreateDirectory_ReturnsDistinctPath_WhenCalledTwice()
        {
            var first = TempConfig.CreateDirectory();
            var second = TempConfig.CreateDirectory();
            try
            {
                Assert.That(first, Is.Not.EqualTo(second));
            }
            finally
            {
                Directory.Delete(first, recursive: true);
                Directory.Delete(second, recursive: true);
            }
        }

        [Test]
        public void MappingFilePath_ReturnsMappingsJsonInsideDirectory()
        {
            var path = TempConfig.MappingFilePath(@"C:\temp\x");

            Assert.That(path, Is.EqualTo(Path.Combine(@"C:\temp\x", "mappings.json")));
        }
    }
}
