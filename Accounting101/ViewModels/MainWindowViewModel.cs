using System.Windows;
using System.Windows.Input;
using Accounting101.Commands;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class MainWindowViewModel
    {
        public ICommand NewCommand { get; }

        public ICommand ExitCommand { get; }

        public MainWindowViewModel(IDataStore dataStore)
        {
            ExitCommand = new DelegateCommand(() => Application.Current.Shutdown());
        }
    }
}