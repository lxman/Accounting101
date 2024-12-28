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

        public bool ClientsExist => _taskFactory.Run(ClientExistsAsync);

        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;

        public MainWindowViewModel(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            MenuViewModel menuViewModel)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            MenuViewModel = menuViewModel;
            if (!_taskFactory.Run(BusinessExistsAsync))
            {
                PresentBusinessCreateScreen();
            }
            else if (!_taskFactory.Run(ClientExistsAsync))
            {
                PresentClientCreateScreen();
            }
            else
            {
                PresentClientListView();
            }
        }

        public void DeleteClient(Guid id)
        {
            _taskFactory.Run(() => _dataStore.DeleteClientAsync(id));
        }

        private void PresentBusinessCreateScreen()
        {
            MenuViewModel.ShowNewBusinessCommand = true;
            MenuViewModel.ShowNewClientCommand = false;
            MenuViewModel.ShowNewAccountCommand = false;
            MenuViewModel.ShowSaveCommand = true;
            InitialScreen = WindowType.CreateBusiness;
        }

        private void PresentClientCreateScreen()
        {
            MenuViewModel.ShowNewBusinessCommand = false;
            MenuViewModel.ShowDeleteBusinessCommand = true;
            MenuViewModel.ShowNewClientCommand = false;
            MenuViewModel.ShowNewAccountCommand = false;
            MenuViewModel.ShowSaveCommand = true;
            MenuViewModel.ShowEditBusinessCommand = true;
            InitialScreen = WindowType.CreateClient;
        }

        private void PresentClientListView()
        {
            MenuViewModel.ShowNewBusinessCommand = false;
            MenuViewModel.ShowDeleteBusinessCommand = true;
            MenuViewModel.ShowDeleteClientCommand = false;
            MenuViewModel.ShowNewClientCommand = true;
            MenuViewModel.ShowNewAccountCommand = false;
            MenuViewModel.ShowSaveCommand = false;
            MenuViewModel.ShowEditBusinessCommand = true;
            InitialScreen = WindowType.ClientList;
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