using System.Windows;
using System.Windows.Input;
using Accounting101.Commands;
using Accounting101.Views.Create;
using Accounting101.Views.List;
using DataAccess;
using DataAccess.Services.Interfaces;
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class MainWindowViewModel : BaseViewModel
    {
        public object PageContent
        {
            get => _pageContent;
            set => SetField(ref _pageContent, value);
        }

        public ICommand NewCommand { get; }

        public ICommand SaveCommand { get; }

        public ICommand ExitCommand { get; }

        private readonly IDataStore _dataStore;
        private object _pageContent;

        public MainWindowViewModel(IDataStore dataStore)
        {
            _dataStore = dataStore;
            ExitCommand = new DelegateCommand(() =>
            {
                _dataStore.Dispose();
                Application.Current.Shutdown();
            });
            if (!BusinessCreated())
            {
                CreateBusinessView createBusinessView = new(_dataStore);
                CreateBusinessViewModel createBusinessViewModel = (CreateBusinessViewModel)createBusinessView.DataContext;
                PageContent = createBusinessView;
                SaveCommand = new DelegateCommand(() => createBusinessViewModel.Save());
            }
            if (!ClientsExist())
            {
                CreateClientView createClientView = new(_dataStore);
                CreateClientViewModel createClientViewModel = (CreateClientViewModel)createClientView.DataContext;
                PageContent = createClientView;
                SaveCommand = new DelegateCommand(() => createClientViewModel.Save());
            }

            ClientListView clientListView = new(_dataStore);
            clientListView.ClientChosen += (sender, id) =>
            {
                ClientChosen(id);
            };
            PageContent = clientListView;
        }

        private void ClientChosen(Guid id)
        {
            PageContent = new ClientAccountsView(_dataStore, id);
            // Switch to individual client
        }

        private bool BusinessCreated()
        {
            return _dataStore.GetBusiness() is not null;
        }

        private bool ClientsExist()
        {
            return _dataStore.AllClients()?.Any() ?? false;
        }
    }
}