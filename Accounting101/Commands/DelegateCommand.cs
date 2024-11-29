using System.Windows.Input;

namespace Accounting101.Commands
{
    public class DelegateCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return true;
        }

        public void Execute(object? parameter)
        {
            action.Invoke();
        }
    }

    public class DelegateCommand<T>(Action<T> action) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        private readonly Func<T, bool>? _canExecute;

        public DelegateCommand(Action<T> action, Func<T, bool> canExecute) : this(action)
        {
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (_canExecute is null)
            {
                return true;
            }

            if (parameter is T t)
            {
                return _canExecute(t);
            }

            return false;
        }

        public void Execute(object? parameter)
        {
            if (parameter is T t)
            {
                action.Invoke(t);
            }
        }
    }
}