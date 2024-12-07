using System.Windows;
using System.Windows.Input;
using Accounting101.Commands;
using Accounting101.Views.Create;
using Accounting101.Views.List;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services;
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

        public bool ShowSaveCommand
        {
            get => _showSaveCommand;
            set => SetField(ref _showSaveCommand, value);
        }

        public ICommand SaveCommand { get; }

        public bool ShowNewCommand
        {
            get => _showNewCommand;
            set => SetField(ref _showNewCommand, value);
        }

        public ICommand ExitCommand { get; }

        private bool _showNewCommand;
        private bool _showSaveCommand;
        private readonly IDataStore _dataStore;
        private object _pageContent;
        private readonly JoinableTaskFactory _taskFactory;

        public MainWindowViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            _dataStore = dataStore;
            _dataStore.StoreChanged += StoreChanged;
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
                SaveCommand = new DelegateCommand(() => BusinessViewSave(createBusinessViewModel));
            }
            if (!taskFactory.Run(ClientsExistAsync))
            {
                CreateClientView createClientView = new(_dataStore, taskFactory);
                CreateClientViewModel createClientViewModel = (CreateClientViewModel)createClientView.DataContext;
                PageContent = createClientView;
                SaveCommand = new DelegateCommand(() => ClientViewSave(createClientViewModel));
            }

            ClientListView clientListView = new(_dataStore, taskFactory);
            clientListView.ClientChosen += (sender, id) =>
            {
                ClientChosen(id);
            };
            PageContent = clientListView;
        }

        private static void StoreChanged(object? sender, ChangeEventArgs e)
        {
            if (e.ChangeType == ChangeType.Created)
            {
                if (e.ChangedType == typeof(Business))
                {
                }
                else if (e.ChangedType == typeof(Client))
                {
                }

                else if (e.ChangedType == typeof(Clients))
                {

                }
                else if (e.ChangedType == typeof(Account))
                {
                }

                else if (e.ChangedType == typeof(Accounts))
                {

                }
                else if (e.ChangedType == typeof(Transaction))
                {
                }
                else if (e.ChangedType == typeof(Transactions))
                {
                }
            }
            else
            {
                if (e.ChangedType == typeof(Transactions))
                {

                }

                if (e.ChangedType == typeof(Transaction))
                {

                }
                if (e.ChangedType == typeof(Accounts))
                {
                }
                if (e.ChangedType == typeof(Account))
                {
                }
                if (e.ChangedType == typeof(Clients))
                {
                }
                if (e.ChangedType == typeof(Client))
                {
                }
                if (e.ChangedType == typeof(Business))
                {
                }
            }
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