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
        public MenuViewModel MenuViewModel { get; }

        public object PageContent
        {
            get => _pageContent;
            set => SetField(ref _pageContent, value);
        }

        private readonly IDataStore _dataStore;
        private object _pageContent;
        private readonly JoinableTaskFactory _taskFactory;

        public MainWindowViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            MenuViewModel = new MenuViewModel(dataStore, taskFactory);
            if (!taskFactory.Run(BusinessExistsAsync))
            {
                PresentBusinessCreateScreen();
            }
            if (!taskFactory.Run(ClientExistsAsync))
            {
                PresentClientCreateScreen();
            }
            PresentClientListView();
        }

        private void PresentBusinessCreateScreen()
        {
            CreateBusinessView createBusinessView = new(_dataStore, _taskFactory);
            CreateBusinessViewModel createBusinessViewModel = (CreateBusinessViewModel)createBusinessView.DataContext;
            PageContent = createBusinessView;
            MenuViewModel.SaveCommand = new DelegateCommand(() => BusinessViewSave(createBusinessViewModel));
        }

        private void PresentClientCreateScreen()
        {
            CreateClientView createClientView = new(_dataStore, _taskFactory);
            CreateClientViewModel createClientViewModel = (CreateClientViewModel)createClientView.DataContext;
            PageContent = createClientView;
            MenuViewModel.SaveCommand = new DelegateCommand(() => ClientViewSave(createClientViewModel));
        }

        private void PresentClientListView()
        {
            ClientListView clientListView = new(_dataStore, _taskFactory);
            clientListView.ClientChosen += (sender, id) =>
            {
                ClientChosen(id);
            };
            PageContent = clientListView;
        }

        private void BusinessViewSave(CreateBusinessViewModel m)
        {
            _taskFactory.Run(m.SaveAsync);
        }

        private void ClientViewSave(CreateClientViewModel m)
        {
            _taskFactory.Run(m.SaveAsync);
        }

        private void ClientChosen(Guid id)
        {
            PageContent = new ClientAccountsView(_dataStore, _taskFactory, id);
        }

        private async Task<bool> BusinessExistsAsync()
        {
            bool businessExists = (await _dataStore.GetBusinessAsync()) is not null;
            MenuViewModel.BusinessExists = businessExists;
            return businessExists;
        }

        private async Task<bool> ClientExistsAsync()
        {
            bool clientExists = (await _dataStore.AllClientsAsync())?.Any() ?? false;
            MenuViewModel.ClientExists = clientExists;
            return clientExists;
        }
    }
}