using System.Windows;
using System.Windows.Input;
using Accounting101.Commands;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class MenuViewModel : BaseViewModel
    {
        public event EventHandler? CreateBusiness;
        public event EventHandler? CreateClient;
        public event EventHandler? CreateAccount;
        public event EventHandler? CreateTransaction;

        public bool ShowNewCommand
        {
            get => _showNewCommand;
            set => SetField(ref _showNewCommand, value);
        }

        public ICommand NewBusinessCommand { get; }

        public bool ShowNewBusinessCommand
        {
            get => _showNewBusinessCommand;
            set => SetField(ref _showNewBusinessCommand, value);
        }

        public ICommand NewClientCommand { get; }

        public bool ShowNewClientCommand
        {
            get => _showNewClientCommand;
            set => SetField(ref _showNewClientCommand, value);
        }

        public ICommand NewAccountCommand { get; }

        public bool ShowNewAccountCommand
        {
            get => _showNewAccountCommand;
            set => SetField(ref _showNewAccountCommand, value);
        }

        public ICommand NewTransactionCommand { get; }

        public bool ShowNewTransactionCommand
        {
            get => _showNewTransactionCommand;
            set => SetField(ref _showNewTransactionCommand, value);
        }

        public ICommand SaveCommand { get; set; }

        public bool ShowSaveCommand
        {
            get => _showSaveCommand;
            set => SetField(ref _showSaveCommand, value);
        }

        public ICommand ExitCommand { get; }

        public bool BusinessExists
        {
            private get => _businessExists;
            set
            {
                _businessExists = value;
                ChangeMenuState();
            }
        }

        public bool ClientExists
        {
            private get => _clientExists;
            set
            {
                _clientExists = value;
                ChangeMenuState();
            }
        }

        public bool AccountExists
        {
            private get => _accountExists;
            set
            {
                _accountExists = value;
                ChangeMenuState();
            }
        }

        private bool _businessExists;
        private bool _clientExists;
        private bool _accountExists;
        private bool _showNewCommand = true;
        private bool _showNewBusinessCommand;
        private bool _showNewClientCommand;
        private bool _showNewAccountCommand;
        private bool _showNewTransactionCommand;
        private bool _showSaveCommand;
        private readonly IDataStore _dataStore;

        public MenuViewModel(IDataStore dataStore)
        {
            _dataStore = dataStore;
            _dataStore.StoreChanged += StoreChanged;
            NewBusinessCommand = new DelegateCommand(() => CreateBusiness?.Invoke(this, EventArgs.Empty));
            NewClientCommand = new DelegateCommand(() => CreateClient?.Invoke(this, EventArgs.Empty));
            NewAccountCommand = new DelegateCommand(() => CreateAccount?.Invoke(this, EventArgs.Empty));
            NewTransactionCommand = new DelegateCommand(() => CreateTransaction?.Invoke(this, EventArgs.Empty));
            ExitCommand = new DelegateCommand(() =>
            {
                _dataStore.Dispose();
                Application.Current.Shutdown();
            });
        }

        private void ChangeMenuState()
        {
            switch (BusinessExists)
            {
                case false:
                    ShowNewBusinessCommand = true;
                    ShowNewClientCommand = false;
                    ShowNewAccountCommand = false;
                    ShowNewTransactionCommand = false;
                    break;
                case true
                    when !ClientExists && !AccountExists:
                    ShowNewBusinessCommand = false;
                    ShowNewClientCommand = true;
                    ShowNewAccountCommand = false;
                    ShowNewTransactionCommand = false;
                    break;
                case true
                    when ClientExists && !AccountExists:
                    ShowNewBusinessCommand = false;
                    ShowNewClientCommand = true;
                    ShowNewAccountCommand = true;
                    ShowNewTransactionCommand = false;
                    break;
                case true
                    when ClientExists && AccountExists:
                    ShowNewBusinessCommand = false;
                    ShowNewClientCommand = true;
                    ShowNewAccountCommand = true;
                    ShowNewTransactionCommand = true;
                    break;
            }
        }

        private void StoreChanged(object? sender, ChangeEventArgs e)
        {
            if (e.ChangeType == ChangeType.Created)
            {
                if (e.ChangedType == typeof(Business))
                {
                    BusinessExists = true;
                }
                else if (e.ChangedType == typeof(Client))
                {
                    ClientExists = true;
                }

                else if (e.ChangedType == typeof(Clients))
                {
                    ClientExists = true;
                }
                else if (e.ChangedType == typeof(Account))
                {
                    AccountExists = true;
                }

                else if (e.ChangedType == typeof(Accounts))
                {
                    AccountExists = true;
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
            ChangeMenuState();
        }
    }
}
