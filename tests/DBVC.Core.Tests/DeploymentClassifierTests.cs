using System;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 저장소 파일은 CREATE OR ALTER로 저장돼 있다. 그것을 그대로 실행해도 되는 경우와
    /// 안 되는 경우를 가르는 것이 배포 스크립트의 전부다.
    /// </summary>
    [TestFixture]
    public class DeploymentClassifierTests
    {
        [TestCase("Table")]
        [TestCase("StoredProcedure")]
        [TestCase("Sequence")]
        public void Classify_Includes_WhenObjectIsMissingInDatabase(string objectType)
        {
            // 신규는 CREATE 그대로라 타입을 가리지 않는다. 테이블도 안전하다.
            Assert.That(
                DeploymentClassifier.Classify(ObjectDiffState.MissingInDatabase, objectType),
                Is.EqualTo(ScriptDisposition.Include));
        }

        [TestCase("StoredProcedure")]
        [TestCase("View")]
        [TestCase("UserDefinedFunction")]
        [TestCase("Trigger")]
        public void Classify_Includes_WhenModifiedTypeSupportsCreateOrAlter(string objectType)
        {
            Assert.That(
                DeploymentClassifier.Classify(ObjectDiffState.Modified, objectType),
                Is.EqualTo(ScriptDisposition.Include));
        }

        [TestCase("Table")]
        [TestCase("Sequence")]
        [TestCase("Synonym")]
        [TestCase("UserDefinedType")]
        public void Classify_RequiresManualChange_WhenModifiedTypeDoesNotSupportCreateOrAlter(string objectType)
        {
            // 기존 테이블에 컬럼을 더하는 것은 기존 행을 무엇으로 채울지의 문제라
            // 스키마만 보고 결정할 수 없다. 틀린 ALTER를 자동 생성하느니 빼는 편이 낫다.
            Assert.That(
                DeploymentClassifier.Classify(ObjectDiffState.Modified, objectType),
                Is.EqualTo(ScriptDisposition.ExcludeManualChange));
        }

        [Test]
        public void Classify_ExcludesNotInBranch_WhenObjectExistsOnlyInDatabase()
        {
            // 브랜치에 파일이 없으므로 스크립트에 담을 재료 자체가 없다.
            Assert.That(
                DeploymentClassifier.Classify(ObjectDiffState.MissingInBranch, "StoredProcedure"),
                Is.EqualTo(ScriptDisposition.ExcludeNotInBranch));
        }

        [Test]
        public void Classify_Throws_WhenStateIsUnknown()
        {
            Assert.Throws<InvalidOperationException>(
                () => DeploymentClassifier.Classify((ObjectDiffState)999, "Table"));
        }
    }
}
