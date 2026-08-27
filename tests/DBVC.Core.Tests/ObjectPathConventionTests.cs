using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class ObjectPathConventionTests
    {
        [Test]
        public void GetRelativePath_FollowsSchemaObjectTypeNameConvention()
        {
            // 설계 4.2: [Schema]/[ObjectType]/[ObjectName].sql
            Assert.That(ObjectPathConvention.GetRelativePath("dbo", "Table", "Users"),
                Is.EqualTo("dbo/Tables/Users.sql"));
        }

        [Test]
        public void GetRelativePath_UsesStoredProceduresFolder_AsDocumentedInDesign()
        {
            // 설계 4.2의 예시: dbo/StoredProcedures/usp_GetUsers.sql
            Assert.That(ObjectPathConvention.GetRelativePath("dbo", "StoredProcedure", "usp_GetUsers"),
                Is.EqualTo("dbo/StoredProcedures/usp_GetUsers.sql"));
        }

        [Test]
        public void GetRelativePath_FallsBackToDbo_WhenSchemaIsMissing()
        {
            Assert.That(ObjectPathConvention.GetRelativePath(null, "Table", "Users"),
                Is.EqualTo("dbo/Tables/Users.sql"));
            Assert.That(ObjectPathConvention.GetRelativePath("   ", "Table", "Users"),
                Is.EqualTo("dbo/Tables/Users.sql"));
        }

        [Test]
        [TestCase("Table", "Tables")]
        [TestCase("View", "Views")]
        [TestCase("StoredProcedure", "StoredProcedures")]
        [TestCase("UserDefinedFunction", "Functions")]
        [TestCase("Trigger", "Triggers")]
        [TestCase("UserDefinedType", "Types")]
        [TestCase("UserDefinedTableType", "TableTypes")]
        [TestCase("Sequence", "Sequences")]
        [TestCase("Synonym", "Synonyms")]
        public void GetFolderName_MapsEverySupportedObjectType(string objectType, string expectedFolder)
        {
            // Feature 14가 요구하는 9개 객체 타입
            Assert.That(ObjectPathConvention.GetFolderName(objectType), Is.EqualTo(expectedFolder));
        }

        [Test]
        [TestCase("TABLE", "Tables")]
        [TestCase("VIEW", "Views")]
        [TestCase("PROCEDURE", "StoredProcedures")]
        [TestCase("FUNCTION", "Functions")]
        [TestCase("TRIGGER", "Triggers")]
        [TestCase("TYPE", "Types")]
        [TestCase("SEQUENCE OBJECT", "Sequences")]
        [TestCase("SYNONYM", "Synonyms")]
        public void GetFolderName_MapsDdlEventDataObjectTypes(string eventDataObjectType, string expectedFolder)
        {
            // DDL 트리거가 기록하는 EVENTDATA의 ObjectType 값도 같은 폴더로 매핑되어야
            // ChangeLog 행에서 파일 경로를 유도할 수 있다.
            Assert.That(ObjectPathConvention.GetFolderName(eventDataObjectType), Is.EqualTo(expectedFolder));
        }

        [Test]
        public void GetFolderName_ReturnsOther_ForUnknownObjectType()
        {
            Assert.That(ObjectPathConvention.GetFolderName("ASSEMBLY"), Is.EqualTo("Other"));
        }

        [Test]
        public void GetQualifiedName_CombinesSchemaAndName()
        {
            Assert.That(ObjectPathConvention.GetQualifiedName("dbo", "Users"), Is.EqualTo("dbo.Users"));
            Assert.That(ObjectPathConvention.GetQualifiedName(null, "Users"), Is.EqualTo("dbo.Users"));
        }

        // ---------- CREATE OR ALTER 지원 타입 ----------

        [TestCase("StoredProcedure")]
        [TestCase("View")]
        [TestCase("UserDefinedFunction")]
        [TestCase("Trigger")]
        public void SupportsCreateOrAlter_ReturnsTrue_ForTheFourTsqlTypes(string objectType)
        {
            Assert.That(ObjectPathConvention.SupportsCreateOrAlter(objectType), Is.True);
        }

        [TestCase("Table")]
        [TestCase("Sequence")]
        [TestCase("Synonym")]
        [TestCase("UserDefinedType")]
        [TestCase("UserDefinedDataType")]
        [TestCase("UserDefinedTableType")]
        public void SupportsCreateOrAlter_ReturnsFalse_ForEveryOtherType(string objectType)
        {
            // 테이블만 빼면 Sequence·Synonym 같은 것들이 조용히 스크립트에 들어가
            // "이미 있습니다"로 실패한다. 축은 "테이블인가"가 아니다.
            Assert.That(ObjectPathConvention.SupportsCreateOrAlter(objectType), Is.False);
        }

        [Test]
        public void SupportsCreateOrAlter_IgnoresCaseAndWhitespace()
        {
            Assert.That(ObjectPathConvention.SupportsCreateOrAlter("  storedprocedure  "), Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("Other")]
        public void SupportsCreateOrAlter_ReturnsFalse_WhenTypeIsUnknown(string? objectType)
        {
            // 모르는 타입은 안전한 쪽으로 떨어뜨린다. 실행 실패보다 "손으로 하세요"가 낫다.
            Assert.That(ObjectPathConvention.SupportsCreateOrAlter(objectType), Is.False);
        }
    }
}
