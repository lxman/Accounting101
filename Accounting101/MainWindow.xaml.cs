using System.Windows;
using Accounting101.Messages;
using Accounting101.ViewModels;
using Accounting101.Views.Create;
using Accounting101.Views.Read;
using Accounting101.Views.Update;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using MahApps.Metro.Controls;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101
{
    public partial class MainWindow
        : MetroWindow,
            IRecipient<ChangeScreenMessage>,
            IRecipient<CreateDatabaseMessage>,
            IRecipient<FocusClientMessage>,
            IRecipient<SetEditAccountVisibleMessage>
    {
        public static readonly DependencyProperty CurrentScreenProperty = DependencyProperty.Register(
            nameof(CurrentScreen), typeof(object), typeof(MainWindow), new PropertyMetadata(default(object)));

        public object CurrentScreen
        {
            get => GetValue(CurrentScreenProperty);
            set => SetValue(CurrentScreenProperty, value);
        }

        public WindowType InitialScreen
        {
            private get => _initialScreen;
            set
            {
                _initialScreen = value;
                SetState();
            }
        }

        public MenuViewModel MenuViewModel { get; }

        private WindowType _initialScreen;
        private readonly JoinableTaskFactory _taskFactory;
        private readonly IDataStore _dataStore;
        private ClientWithInfo? _client;
        private Guid? _accountId;

        public MainWindow(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            MainWindowViewModel mainWindowViewModel,
            MenuViewModel menuViewModel)
        {
            WeakReferenceMessenger.Default.RegisterAll(this);
            _taskFactory = taskFactory;
            _dataStore = dataStore;
            DataContext = mainWindowViewModel;
            MenuViewModel = menuViewModel;
            InitializeComponent();
        }

        public void Receive(SetEditAccountVisibleMessage message)
        {
            if (message.Value is null)
            {
                _accountId = null;
                MenuViewModel.ShowEditAccountCommand = false;
                return;
            }
            _accountId = message.Value;
            MenuViewModel.ShowEditAccountCommand = true;
        }

        public void Receive(ChangeScreenMessage message)
        {
            if (_dataStore.Initialized)
            {
                if (!MenuViewModel.ClientExists)
                {
                    MenuViewModel.ClientExists = _taskFactory.Run(_dataStore.ClientsExistAsync);
                }
            }
            switch (message.Value)
            {
                case WindowType.CreateBusiness:
                    MenuViewModel.CurrentScreen = WindowType.CreateBusiness;
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateBusinessView)?.Save()));
                    CurrentScreen = new CreateBusinessView(_dataStore, _taskFactory);
                    break;
                case WindowType.GetPassword:
                    MenuViewModel.CurrentScreen = WindowType.GetPassword;
                    CurrentScreen = new GetPasswordView(_dataStore, _taskFactory);
                    break;
                case WindowType.CreateClient:
                    MenuViewModel.CurrentScreen = WindowType.CreateClient;
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateClientView)?.Save()));
                    CurrentScreen = new CreateClientView(_dataStore, _taskFactory);
                    break;
                case WindowType.ClientList:
                    MenuViewModel.CurrentScreen = WindowType.ClientList;
                    CurrentScreen = new ClientListView(_dataStore, _taskFactory);
                    _client = null;
                    break;
                case WindowType.ClientAccountList:
                    if (_client is null)
                    {
                        return;
                    }

                    MenuViewModel.CurrentScreen = WindowType.ClientAccountList;
                    CurrentScreen = new ClientWithAccountListView(_dataStore, _taskFactory, _client);
                    break;
                case WindowType.CreateAccount:
                    if (_client is null)
                    {
                        return;
                    }

                    MenuViewModel.CurrentScreen = WindowType.CreateAccount;
                    CreateAccountView createAccountView = new();
                    createAccountView.SetInfo(_dataStore, _taskFactory, _client);
                    CurrentScreen = createAccountView;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => createAccountView.Save()));
                    MenuViewModel.ShowSaveCommand = true;
                    break;
                case WindowType.EditBusiness:
                    MenuViewModel.CurrentScreen = WindowType.EditBusiness;
                    UpdateBusinessView updateBusinessView = new();
                    updateBusinessView.SetInfo(_dataStore, _taskFactory);
                    CurrentScreen = updateBusinessView;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => updateBusinessView.Save()));
                    MenuViewModel.ShowSaveCommand = true;
                    break;
                case WindowType.EditClient:
                    MenuViewModel.CurrentScreen = WindowType.EditClient;
                    break;
                case WindowType.EditAccount:
                    MenuViewModel.CurrentScreen = WindowType.EditAccount;
                    break;
                case WindowType.BalanceSheet:
                    MenuViewModel.CurrentScreen = WindowType.BalanceSheet;
                    break;
                case WindowType.ProfitAndLoss:
                    MenuViewModel.CurrentScreen = WindowType.ProfitAndLoss;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public void Receive(CreateDatabaseMessage message)
        {
            _dataStore.CreateDatabase(_dataStore.GetDbLocation());
        }

        public void Receive(FocusClientMessage message)
        {
            _client = message.Value;
            Receive(new ChangeScreenMessage(WindowType.ClientAccountList));
        }

        private void SetState()
        {
            switch (InitialScreen)
            {
                case WindowType.SetupDatabase:
                    MenuViewModel.CurrentScreen = WindowType.SetupDatabase;
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateDatabaseView)?.Save()));
                    break;
                case WindowType.CreateBusiness:
                    MenuViewModel.CurrentScreen = WindowType.CreateBusiness;
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateBusinessView)?.Save()));
                    break;
                case WindowType.CreateClient:
                    MenuViewModel.CurrentScreen = WindowType.CreateClient;
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateClientView)?.Save()));
                    break;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Application.Current.Shutdown();
        }
    }
}