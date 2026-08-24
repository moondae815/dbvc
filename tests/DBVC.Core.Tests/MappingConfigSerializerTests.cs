using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// mappings.json은 사용자가 손으로 고칠 수 있는 파일이다. 값이 빠지거나 틀렸을 때
    /// 어느 쪽으로 실패하는지가 안전에 직결되므로 여기서 못박는다.
    /// </summary>
    [TestFixture]
    public class MappingConfigSerializerTests
    {
        [Test]
        public void Deserialize_DefaultsToWriteAndFreeBranch_WhenFieldsAreAbsent()
        {
            // 0.2.x가 만든 파일이다. 이 형식이 그대로 읽히지 않으면 기존 사용자의 매핑이 전부 사라진다.
            var json = @"[{""ServerName"":""localhost"",""DatabaseName"":""db1"",""GitPath"":""C:\\repo""}]";

            var result = MappingConfigSerializer.Deserialize(json);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result![0].Mode, Is.EqualTo(MappingMode.Write));
            Assert.That(result[0].Branch, Is.Null);
        }

        [Test]
        public void Deserialize_ReadsBranchAndMode_WhenPresent()
        {
            var json = @"[{""ServerName"":""s"",""DatabaseName"":""d"",""GitPath"":""p"",""Branch"":""master"",""Mode"":""Audit""}]";

            var result = MappingConfigSerializer.Deserialize(json);

            Assert.That(result![0].Branch, Is.EqualTo("master"));
            Assert.That(result[0].Mode, Is.EqualTo(MappingMode.Audit));
        }

        [Test]
        public void Deserialize_FallsBackToAudit_WhenModeIsUnknown()
        {
            // 오타로 권한이 넓어지면 안 된다. 모르는 값은 가장 제한적인 쪽으로 읽는다.
            var json = @"[{""ServerName"":""s"",""DatabaseName"":""d"",""GitPath"":""p"",""Mode"":""audi""}]";

            var result = MappingConfigSerializer.Deserialize(json);

            Assert.That(result![0].Mode, Is.EqualTo(MappingMode.Audit));
        }

        [Test]
        public void Deserialize_IsCaseInsensitiveForMode()
        {
            var json = @"[{""ServerName"":""s"",""DatabaseName"":""d"",""GitPath"":""p"",""Mode"":""deploy""}]";

            var result = MappingConfigSerializer.Deserialize(json);

            Assert.That(result![0].Mode, Is.EqualTo(MappingMode.Deploy));
        }

        [Test]
        public void Serialize_WritesModeAsString()
        {
            var mappings = new List<MappingConfig>
            {
                new MappingConfig { ServerName = "s", DatabaseName = "d", GitPath = "p", Mode = MappingMode.Deploy }
            };

            var json = MappingConfigSerializer.Serialize(mappings);

            // 숫자로 나가면 사람이 파일을 읽고 고칠 수 없다.
            Assert.That(json, Does.Contain("\"Deploy\""));
        }
    }
}
