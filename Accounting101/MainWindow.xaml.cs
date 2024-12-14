using System.Windows;
using Accounting101.Messages;
using Accounting101.ViewModels;
using Accounting101.Views.Create;
using Accounting101.Views.List;
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

        public MainWindow(IDataStore dataStore, MainWindowViewModel vm)
        {
            WeakReferenceMessenger.Default.Register(this);
            _mainWindowViewModel = vm;
            _menuViewModel = vm.MenuViewModel;
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
                    PresentClientListView();
                    break;
                case WindowType.ClientAccountList:
                    break;
                case WindowType.CreateAccount:
                    PresentAccountCreateScreen();
                    break;
                case WindowType.CreateTransaction:
                    PresentTransactionCreateScreen();
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

        private void PresentClientListView()
        {
            ClientListView clientListView = new(_dataStore, _taskFactory);
            clientListView.ClientChosen += (sender, id) =>
            {
                ClientChosen(id);
            };
            CurrentScreen = clientListView;
            SetInitialScreen(clientListView);
            _menuViewModel.ActiveWindow = WindowType.ClientList;
        }

        private void PresentAccountCreateScreen()
        {
        }

        private void PresentTransactionCreateScreen()
        {
        }

        private void ClientChosen(Guid id)
        {
            CurrentScreen = new ClientAccountsView(_dataStore, _taskFactory, id);
        }

        private void DeleteClient()
        {
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