using System;
using DBVC.Core;
using DBVC.Vsix.ViewModels;
using NUnit.Framework;

namespace DBVC.Vsix.Tests.ViewModels
{
    [TestFixture]
    public class HistoryChangedFileViewModelTests
    {
        [TestCase(HistoryChangedFileState.Added, "추가")]
        [TestCase(HistoryChangedFileState.Modified, "수정")]
        [TestCase(HistoryChangedFileState.Deleted, "삭제")]
        public void StateText_TranslatesHistoryChangedFileState(HistoryChangedFileState state, string expected)
        {
            var vm = new HistoryChangedFileViewModel { State = state };
            Assert.That(vm.StateText, Is.EqualTo(expected));
        }

        [TestCase("dbo/Tables/Users.sql", "dbo.Users", "Table", "Table")]
        [TestCase("dbo/StoredProcedures/usp_GetUsers.sql", "dbo.usp_GetUsers", "StoredProcedure", "SP")]
        [TestCase("dbo/Functions/fn_Calculate.sql", "dbo.fn_Calculate", "UserDefinedFunction", "UDF")]
        [TestCase("dbo/Views/vw_Report.sql", "dbo.vw_Report", "View", "View")]
        [TestCase("dbo/Triggers/tr_Audit.sql", "dbo.tr_Audit", "Trigger", "Trigger")]
        public void From_ExtractsObjectNameAndObjectType_WhenStandardPath(string path, string expectedName, string expectedType, string expectedTypeText)
        {
            var file = new HistoryChangedFile
            {
                RelativePath = path,
                State = HistoryChangedFileState.Modified
            };

            var vm = HistoryChangedFileViewModel.From(file);

            Assert.That(vm.ObjectName, Is.EqualTo(expectedName));
            Assert.That(vm.ObjectType, Is.EqualTo(expectedType));
            Assert.That(vm.ObjectTypeText, Is.EqualTo(expectedTypeText));
            Assert.That(vm.RelativePath, Is.EqualTo(path));
            Assert.That(vm.State, Is.EqualTo(HistoryChangedFileState.Modified));
            Assert.That(vm.StateText, Is.EqualTo("수정"));
        }

        [Test]
        public void From_HandlesNonStandardPathGracefully()
        {
            var file = new HistoryChangedFile
            {
                RelativePath = "README.md",
                State = HistoryChangedFileState.Added
            };

            var vm = HistoryChangedFileViewModel.From(file);

            Assert.That(vm.ObjectName, Is.EqualTo("README.md"));
            Assert.That(vm.ObjectType, Is.EqualTo(string.Empty));
            Assert.That(vm.ObjectTypeText, Is.EqualTo(string.Empty));
            Assert.That(vm.RelativePath, Is.EqualTo("README.md"));
            Assert.That(vm.State, Is.EqualTo(HistoryChangedFileState.Added));
            Assert.That(vm.StateText, Is.EqualTo("추가"));
        }

        [Test]
        public void From_ThrowsArgumentNullException_WhenFileIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => HistoryChangedFileViewModel.From(null!));
        }
    }
}
