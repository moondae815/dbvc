using System;
using System.ComponentModel.Design;
using DBVC.Vsix.Commands;
using DBVC.Vsix.Services;
using Microsoft.VisualStudio.Shell;
using Moq;
using NUnit.Framework;

namespace DBVC.Vsix.Tests.Commands
{
    [TestFixture]
    public class ShowHistoryCommandTests
    {
        [Test]
        public void CommandConstants_MatchExpectedValues()
        {
            Assert.That(ShowHistoryCommand.CommandId, Is.EqualTo(0x0102));
            Assert.That(ShowHistoryCommand.CommandSet, Is.EqualTo(new Guid("5c9e7b22-1d3f-4a68-b0c4-9e7d5f2a3b14")));
        }
    }
}
