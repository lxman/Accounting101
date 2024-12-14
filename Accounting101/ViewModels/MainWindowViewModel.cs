using DataAccess;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable VSTHRD002
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class MainWindowViewModel : BaseViewModel
    {
        public WindowType InitialScreen { get; private set; }

        public MenuViewModel MenuViewModel { get; }

        private readonly IDataStore _dataStore;

        public MainWindowViewModel(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            MenuViewModel menuViewModel)
        {
            _dataStore = dataStore;
            MenuViewModel = menuViewModel;
            if (!taskFactory.Run(BusinessExistsAsync))
            {
                PresentBusinessCreateScreen();
            }
            else if (!taskFactory.Run(ClientExistsAsync))
            {
                PresentClientCreateScreen();
            }
            else
            {
                PresentClientListView();
            }
        }

        private void PresentBusinessCreateScreen()
        {
            MenuViewModel.ShowNewBusinessCommand = true;
            MenuViewModel.ShowNewClientCommand = false;
            MenuViewModel.ShowNewAccountCommand = false;
            MenuViewModel.ShowNewTransactionCommand = false;
            MenuViewModel.ShowSaveCommand = true;
            InitialScreen = WindowType.CreateBusiness;
        }

        private void PresentClientCreateScreen()
        {
            MenuViewModel.ShowNewBusinessCommand = false;
            MenuViewModel.ShowDeleteCommand = true;
            MenuViewModel.ShowDeleteBusinessCommand = true;
            MenuViewModel.ShowNewClientCommand = true;
            MenuViewModel.ShowNewAccountCommand = false;
            MenuViewModel.ShowNewTransactionCommand = false;
            MenuViewModel.ShowSaveCommand = true;
            InitialScreen = WindowType.CreateClient;
        }

        private void PresentClientListView()
        {
            MenuViewModel.ShowNewBusinessCommand = false;
            MenuViewModel.ShowDeleteCommand = true;
            MenuViewModel.ShowDeleteBusinessCommand = true;
            MenuViewModel.ShowDeleteClientCommand = true;
            MenuViewModel.ShowNewClientCommand = true;
            MenuViewModel.ShowNewAccountCommand = true;
            MenuViewModel.ShowNewTransactionCommand = false;
            MenuViewModel.ShowSaveCommand = true;
            InitialScreen = WindowType.ClientList;
        }

        private void PresentAccountCreateScreen()
        {
        }

        private void PresentTransactionCreateScreen()
        {
        }

        private async Task<bool> BusinessExistsAsync()
        {
            bool businessExists = await _dataStore.GetBusinessAsync() is not null;
            return businessExists;
        }

        private async Task<bool> ClientExistsAsync()
        {
            bool clientExists = (await _dataStore.AllClientsAsync())?.Any() ?? false;
            return clientExists;
        }
    }
}