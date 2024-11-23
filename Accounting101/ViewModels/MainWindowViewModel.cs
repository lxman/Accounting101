using System.Windows;
using System.Windows.Input;
using Accounting101.Commands;
using Accounting101.Views.Create;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class MainWindowViewModel
    {
        public object PageContent { get; }

        public ICommand NewCommand { get; }

        public ICommand SaveCommand { get; }

        public ICommand ExitCommand { get; }

        private readonly IDataStore _dataStore;

        public MainWindowViewModel(IDataStore dataStore)
        {
            _dataStore = dataStore;
            ExitCommand = new DelegateCommand(() => Application.Current.Shutdown());
            if (!BusinessCreated())
            {
                PageContent = new CreateBusinessView(_dataStore);
            }
        }

        private bool BusinessCreated()
        {
            return _dataStore.GetBusiness() is not null;
        }
    }
}