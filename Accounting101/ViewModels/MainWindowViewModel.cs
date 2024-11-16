using System.Windows;
using System.Windows.Input;
using Accounting101.Commands;
using Accounting101.Views.List;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class MainWindowViewModel
    {
        public ClientListControl ClientListControl { get; }

        public ICommand NewCommand { get; }

        public ICommand ExitCommand { get; }

        public MainWindowViewModel(IDataStore dataStore)
        {
            ClientListControl = new ClientListControl(dataStore);
            ExitCommand = new DelegateCommand(() => Application.Current.Shutdown());
        }
    }
}