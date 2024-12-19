using System.Windows;
using Accounting101.Messages;
using Accounting101.ViewModels;
using Accounting101.Views.Create;
using Accounting101.Views.List;
using Accounting101.Views.Update;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101
{
    public partial class MainWindow : Window, IRecipient<ChangeScreenMessage>
    {
        public object InitialScreen { get; private set; }

        public static readonly DependencyProperty CurrentScreenProperty = DependencyProperty.Register(
            nameof(CurrentScreen), typeof(object), typeof(MainWindow), new PropertyMetadata(default(object)));

        public object CurrentScreen
        {
            get => GetValue(CurrentScreenProperty);
            set => SetValue(CurrentScreenProperty, value);
        }

        private readonly JoinableTaskFactory _taskFactory;
        private readonly IDataStore _dataStore;
        private bool _initialScreenSet;
        private readonly MainWindowViewModel _mainWindowViewModel;
        private readonly MenuViewModel _menuViewModel;
        private Guid? _currentClientId;

        public MainWindow(IDataStore dataStore, MainWindowViewModel vm)
        {
            WeakReferenceMessenger.Default.Register(this);
            _mainWindowViewModel = vm;
            _menuViewModel = vm.MenuViewModel;
            _menuViewModel.DeleteClient += DeleteClient;
            _taskFactory = new JoinableTaskFactory(new JoinableTaskCollection(new JoinableTaskContext()));
            _dataStore = dataStore;
            DataContext = vm;
            InitializeComponent();
            ShowScreen(vm.InitialScreen);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Application.Current.Shutdown();
        }

        public void Receive(ChangeScreenMessage message)
        {
            ShowScreen(message.Value);
        }

        private void ShowScreen(WindowType type)
        {
            switch (type)
            {
                case WindowType.CreateBusiness:
                    PresentBusinessCreateScreen();
                    break;

                case WindowType.CreateClient:
                    PresentClientCreateScreen();
                    break;

                case WindowType.ClientList:
                    PresentClientListViewScreen();
                    break;

                case WindowType.ClientAccountList:
                    PresentClientAccountListViewScreen();
                    break;

                case WindowType.CreateAccount:
                    PresentAccountCreateScreen();
                    break;

                case WindowType.EditBusiness:
                    PresentBusinessEditScreen();
                    break;

                case WindowType.EditClient:
                    PresentClientEditScreen();
                    break;

                case WindowType.EditAccount:
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        private void PresentBusinessCreateScreen()
        {
            CreateBusinessView createBusinessView = new(_dataStore, _taskFactory);
            CurrentScreen = createBusinessView;
            SetInitialScreen(createBusinessView);
            _menuViewModel.ActiveWindow = WindowType.CreateBusiness;
        }

        private void PresentClientCreateScreen()
        {
            CreateClientView createClientView = new(_dataStore, _taskFactory);
            CurrentScreen = createClientView;
            SetInitialScreen(createClientView);
            _menuViewModel.ActiveWindow = WindowType.CreateClient;
        }

        private void PresentClientListViewScreen()
        {
            _currentClientId = null;
            _menuViewModel.ShowNewAccountCommand = false;
            _menuViewModel.ShowDeleteClientCommand = false;
            _menuViewModel.ShowEditClientCommand = false;
            _menuViewModel.ShowReportsBalanceSheetCommand = false;
            _menuViewModel.ShowReportsProfitAndLossCommand = false;
            ClientListView clientListView = new(_dataStore, _taskFactory);
            clientListView.ClientChosen += (sender, id) =>
            {
                _currentClientId = id;
                _menuViewModel.ShowNewAccountCommand = true;
                _menuViewModel.ShowDeleteClientCommand = true;
                _menuViewModel.ShowEditClientCommand = true;
                _menuViewModel.ShowReportsBalanceSheetCommand = true;
                _menuViewModel.ShowReportsProfitAndLossCommand = true;
                ClientChosen(id);
            };
            CurrentScreen = clientListView;
            SetInitialScreen(clientListView);
            _menuViewModel.ActiveWindow = WindowType.ClientList;
        }

        private void PresentClientAccountListViewScreen()
        {
            if (!_currentClientId.HasValue)
            {
                return;
            }
            ClientChosen(_currentClientId.Value);
        }

        private void PresentAccountCreateScreen()
        {
            if (!_currentClientId.HasValue)
            {
                return;
            }
            CreateAccountView createAccountView = new(_dataStore, _taskFactory, _currentClientId.Value);
            CurrentScreen = createAccountView;
            SetInitialScreen(createAccountView);
            _menuViewModel.ActiveWindow = WindowType.CreateAccount;
        }

        private void PresentBusinessEditScreen()
        {
            _currentClientId = null;
            UpdateBusinessView updateBusinessView = new(_dataStore, _taskFactory, _mainWindowViewModel.ClientsExist);
            CurrentScreen = updateBusinessView;
            SetInitialScreen(updateBusinessView);
            _menuViewModel.ActiveWindow = WindowType.EditBusiness;
        }

        private void PresentClientEditScreen()
        {
            if (_currentClientId == null)
            {
                PresentClientListViewScreen();
                return;
            }

            UpdateClientView updateClientView = new(_dataStore, _taskFactory, _currentClientId.Value);
            CurrentScreen = updateClientView;
            SetInitialScreen(updateClientView);
            _menuViewModel.ActiveWindow = WindowType.EditClient;
        }

        private void ClientChosen(Guid id)
        {
            CurrentScreen = new ClientAccountsView(_dataStore, _taskFactory, id);
        }

        private void DeleteClient(object? sender, EventArgs e)
        {
            if (!_currentClientId.HasValue)
            {
                return;
            }
            _mainWindowViewModel.DeleteClient(_currentClientId.Value);
            PresentClientListViewScreen();
        }

        private void SetInitialScreen(object screen)
        {
            if (_initialScreenSet)
            {
                return;
            }
            InitialScreen = screen;
            _initialScreenSet = true;
        }
    }
}