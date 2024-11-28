using System.Windows;
using System.Windows.Input;
using Accounting101.Commands;
using Accounting101.Views.Create;
using Accounting101.Views.List;
using DataAccess;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable VSTHRD002
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
        private readonly JoinableTaskFactory _taskFactory;

        public MainWindowViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            ExitCommand = new DelegateCommand(() =>
            {
                _dataStore.Dispose();
                Application.Current.Shutdown();
            });
            if (!taskFactory.Run(BusinessCreatedAsync))
            {
                CreateBusinessView createBusinessView = new(_dataStore, taskFactory);
                CreateBusinessViewModel createBusinessViewModel = (CreateBusinessViewModel)createBusinessView.DataContext;
                PageContent = createBusinessView;
                SaveCommand = new DelegateCommand(async void () => await createBusinessViewModel.SaveAsync());
            }
            if (!taskFactory.Run(ClientsExistAsync))
            {
                CreateClientView createClientView = new(_dataStore, taskFactory);
                CreateClientViewModel createClientViewModel = (CreateClientViewModel)createClientView.DataContext;
                PageContent = createClientView;
                SaveCommand = new DelegateCommand(async void () => await createClientViewModel.SaveAsync());
            }

            ClientListView clientListView = new(_dataStore, taskFactory);
            clientListView.ClientChosen += (sender, id) =>
            {
                ClientChosen(id);
            };
            PageContent = clientListView;
        }

        private void ClientChosen(Guid id)
        {
            PageContent = new ClientAccountsView(_dataStore, _taskFactory, id);
        }

        private async Task<bool> BusinessCreatedAsync()
        {
            return (await _dataStore.GetBusinessAsync()) is not null;
        }

        private async Task<bool> ClientsExistAsync()
        {
            return (await _dataStore.AllClientsAsync())?.Any() ?? false;
        }
    }
}