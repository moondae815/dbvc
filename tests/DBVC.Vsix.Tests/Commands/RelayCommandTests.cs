using System;
using NUnit.Framework;
using DBVC.Vsix.Commands;

namespace DBVC.Vsix.Tests.Commands
{
    [TestFixture]
    public class RelayCommandTests
    {
        [Test]
        public void Execute_InvokesAction()
        {
            bool executed = false;
            var command = new RelayCommand(() => executed = true);

            command.Execute(null);

            Assert.That(executed, Is.True);
        }

        [Test]
        public void CanExecute_ReturnsTrueByDefault()
        {
            var command = new RelayCommand(() => { });

            Assert.That(command.CanExecute(null), Is.True);
        }

        [Test]
        public void CanExecute_EvaluatesPredicate()
        {
            bool allowed = false;
            var command = new RelayCommand(() => { }, () => allowed);

            Assert.That(command.CanExecute(null), Is.False);

            allowed = true;
            Assert.That(command.CanExecute(null), Is.True);
        }

        [Test]
        public void RaiseCanExecuteChanged_FiresEvent()
        {
            var command = new RelayCommand(() => { });
            bool fired = false;
            command.CanExecuteChanged += (s, e) => fired = true;

            command.RaiseCanExecuteChanged();

            Assert.That(fired, Is.True);
        }

        [Test]
        public void Constructor_NullExecute_ThrowsArgumentNullException()
        {
            Action execute = null!;
            Assert.Throws<ArgumentNullException>(() => new RelayCommand(execute));
        }
    }
}
