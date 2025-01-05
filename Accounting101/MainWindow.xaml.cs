using System.Windows;
using Accounting101.Messages;
using Accounting101.ViewModels;
using Accounting101.Views.Create;
using Accounting101.Views.Read;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using MahApps.Metro.Controls;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101
{
    public partial class MainWindow : MetroWindow, IRecipient<ChangeScreenMessage>, IRecipient<CreateDatabaseMessage>, IRecipient<FocusClientMessage>
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

        public MainWindow(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            MainWindowViewModel mainWindowViewModel,
            MenuViewModel menuViewModel)
        {
            WeakReferenceMessenger.Default.Register<ChangeScreenMessage>(this);
            WeakReferenceMessenger.Default.Register<CreateDatabaseMessage>(this);
            WeakReferenceMessenger.Default.Register<FocusClientMessage>(this);
            _taskFactory = taskFactory;
            _dataStore = dataStore;
            DataContext = mainWindowViewModel;
            MenuViewModel = menuViewModel;
            InitializeComponent();
        }

        public void Receive(ChangeScreenMessage message)
        {
            switch (message.Value)
            {
                case WindowType.CreateBusiness:
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateBusinessView)?.Save()));
                    CurrentScreen = new CreateBusinessView(_dataStore, _taskFactory);
                    break;
                case WindowType.GetPassword:
                    CurrentScreen = new GetPasswordView(_dataStore, _taskFactory);
                    break;
                case WindowType.CreateClient:
                    MenuViewModel.ShowDeleteBusinessCommand = true;
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateClientView)?.Save()));
                    CurrentScreen = new CreateClientView(_dataStore, _taskFactory);
                    break;
                case WindowType.ClientList:
                    CurrentScreen = new ClientListView(_dataStore, _taskFactory);
                    _client = null;
                    break;
                case WindowType.ClientAccountList:
                    if (_client is null)
                    {
                        return;
                    }
                    CurrentScreen = new ClientWithAccountListView(_dataStore, _taskFactory, _client);
                    break;
                case WindowType.CreateAccount:
                    break;
                case WindowType.EditBusiness:
                    break;
                case WindowType.EditClient:
                    break;
                case WindowType.EditAccount:
                    break;
                case WindowType.BalanceSheet:
                    break;
                case WindowType.ProfitAndLoss:
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
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateDatabaseView)?.Save()));
                    break;
                case WindowType.CreateBusiness:
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateBusinessView)?.Save()));
                    break;
                case WindowType.CreateClient:
                    MenuViewModel.ShowDeleteBusinessCommand = true;
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