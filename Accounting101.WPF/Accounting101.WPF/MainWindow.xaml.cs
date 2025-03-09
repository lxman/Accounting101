using System.IO;
using System.Windows;
using System.Windows.Input;
using Accounting101.WPF.Messages;
using Accounting101.WPF.ViewModels;
using Accounting101.WPF.Views.Create;
using Accounting101.WPF.Views.Read;
using Accounting101.WPF.Views.Read.Reports;
using Accounting101.WPF.Views.Update;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services;
using DataAccess.WPF.Services.Interfaces;
using MahApps.Metro.Controls.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.Threading;

// ReSharper disable SwitchStatementHandlesSomeKnownEnumValuesWithDefault
// ReSharper disable RedundantJumpStatement
// ReSharper disable AsyncVoidMethod
#pragma warning disable VSTHRD100
#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF;

public partial class MainWindow
    : IRecipient<ChangeScreenMessage>,
        IRecipient<CreateDatabaseMessage>,
        IRecipient<FocusClientMessage>,
        IRecipient<SetEditAccountVisibleMessage>,
        IRecipient<BusinessEditedMessage>,
        IRecipient<EditingTransactionMessage>,
        IRecipient<EditAccountMessage>
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
            MenuViewModel.CurrentScreen = value;
            SetState();
        }
    }

    public MenuViewModel MenuViewModel { get; }

    private WindowType _initialScreen;
    private bool _viewingReport;
    private readonly JoinableTaskFactory _taskFactory;
    private IDataStore _dataStore;
    private List<string>? _states;
    private ClientWithInfo? _client;
    private Guid? _accountId;
    private bool _enableTransactionKeyWatcher;
    private bool _enableEditingKeyWatcher;
    private readonly IServiceCollection _serviceCollection;

    private static readonly List<Key> TransactionKeys =
    [
        Key.E,
        Key.Delete,
        Key.N
    ];

    private static readonly List<Key> EditingKeys =
    [
        Key.C,
        Key.D,
        Key.Escape,
        Key.Return,
        Key.Tab
    ];

    public MainWindow(
        IDataStore dataStore,
        JoinableTaskFactory taskFactory,
        MenuViewModel menuViewModel,
        IServiceCollection serviceCollection)
    {
        WeakReferenceMessenger.Default.RegisterAll(this);
        _taskFactory = taskFactory;
        _dataStore = dataStore;
        _serviceCollection = serviceCollection;
        DataContext = this;
        MenuViewModel = menuViewModel;
        InitializeComponent();
        MenuViewModel.DeleteClient += DeleteClient;
        MenuViewModel.DeleteBusiness += DeleteBusiness;
    }

    public void Receive(BusinessEditedMessage message)
    {
        if (MenuViewModel.ClientExists)
        {
            Receive(new ChangeScreenMessage(WindowType.ClientList));
            return;
        }
        Receive(new ChangeScreenMessage(WindowType.CreateClient));
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

    public void Receive(EditAccountMessage message)
    {
        if (_accountId is null || !message.Value)
        {
            return;
        }
        if (CurrentScreen is ClientWithAccountListView clientWithAccountListView)
        {
            clientWithAccountListView.UpdateAccountInfo(_taskFactory.Run(() => _dataStore.GetAccountWithInfoAsync(_accountId.Value)) ?? new AccountWithInfo());
            MenuViewModel.SetSaveCommand(new RelayCommand(() => clientWithAccountListView.SaveAccountChanges()));
            MenuViewModel.ShowSaveCommand = true;
        }
    }

    public void Receive(ChangeScreenMessage message)
    {
        if (message.Value == WindowType.SetupDatabase)
        {
            _enableTransactionKeyWatcher = false;
            MenuViewModel.CurrentScreen = WindowType.SetupDatabase;
            MenuViewModel.ShowSaveCommand = true;
            MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateDatabaseView)?.Save()));
            CurrentScreen = new CreateDatabaseView();
            return;
        }
        while (true)
        {
            if (_dataStore.Initialized)
            {
                if (!MenuViewModel.ClientExists)
                {
                    MenuViewModel.ClientExists = _taskFactory.Run(_dataStore.ClientsExistAsync);
                }
            }

            _states ??= _taskFactory.Run(_dataStore.GetStatesAsync).Order().ToList();
            switch (message.Value)
            {
                case WindowType.CreateBusiness:
                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.CreateBusiness;
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateBusinessView)?.Save()));
                    CurrentScreen = new CreateBusinessView(_dataStore, _taskFactory);
                    break;

                case WindowType.GetPassword:
                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.GetPassword;
                    CurrentScreen = new GetPasswordView(_dataStore, _taskFactory);
                    break;

                case WindowType.CreateClient:
                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.CreateClient;
                    MenuViewModel.ShowSaveCommand = true;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => (CurrentScreen as CreateClientView)?.Save()));
                    CurrentScreen = new CreateClientView(_dataStore, _taskFactory);
                    break;

                case WindowType.ClientList:
                    if (_viewingReport)
                    {
                        _viewingReport = false;
                        message = new ChangeScreenMessage(WindowType.ClientAccountList);
                        continue;
                    }

                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.ClientList;
                    CurrentScreen = new ClientListView(_dataStore, _taskFactory);
                    _client = null;
                    break;

                case WindowType.ClientAccountList:
                    if (_client is null)
                    {
                        return;
                    }

                    _enableTransactionKeyWatcher = true;
                    MenuViewModel.CurrentScreen = WindowType.ClientAccountList;
                    CurrentScreen = new ClientWithAccountListView(_dataStore, _taskFactory, _client);
                    break;

                case WindowType.CreateAccount:
                    if (_client is null)
                    {
                        return;
                    }

                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.CreateAccount;
                    CreateAccountView createAccountView = new();
                    createAccountView.SetInfo(_dataStore, _taskFactory, _client);
                    CurrentScreen = createAccountView;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => createAccountView.Save()));
                    MenuViewModel.ShowSaveCommand = true;
                    break;

                case WindowType.EditBusiness:
                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.EditBusiness;
                    UpdateBusinessView updateBusinessView = new();
                    updateBusinessView.SetInfo(_dataStore, _taskFactory);
                    CurrentScreen = updateBusinessView;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => updateBusinessView.Save()));
                    MenuViewModel.ShowSaveCommand = true;
                    break;

                case WindowType.EditClient:
                    if (_client is null)
                    {
                        return;
                    }

                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.EditClient;
                    UpdateClientView updateClientView = new();
                    updateClientView.SetInfo(_dataStore, _taskFactory, _client, _states);
                    CurrentScreen = updateClientView;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => updateClientView.Save()));
                    MenuViewModel.ShowSaveCommand = true;
                    break;

                case WindowType.EditAccount:
                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.EditAccount;
                    break;

                case WindowType.BalanceSheet:
                    if (_client is null)
                    {
                        return;
                    }

                    _viewingReport = true;
                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.BalanceSheet;
                    CurrentScreen = new BalanceSheetView(_dataStore, _taskFactory, _client.Id);
                    MenuViewModel.ShowSaveCommand = false;
                    break;

                case WindowType.ProfitAndLoss:
                    if (_client is null)
                    {
                        return;
                    }

                    _viewingReport = true;
                    _enableTransactionKeyWatcher = false;
                    MenuViewModel.CurrentScreen = WindowType.ProfitAndLoss;
                    CurrentScreen = new ProfitAndLossView(_dataStore, _taskFactory, _client.Id);
                    MenuViewModel.ShowSaveCommand = false;
                    break;

                case WindowType.UpdateTheme:
                    _enableTransactionKeyWatcher = false;
                    CurrentScreen = new UpdateThemeView();
                    MenuViewModel.CurrentScreen = WindowType.UpdateTheme;
                    break;

                case WindowType.CheckPoints:
                    if (_client is null)
                    {
                        return;
                    }

                    _enableTransactionKeyWatcher = false;
                    CreateCheckPointView createCheckPointView = new();
                    createCheckPointView.SetInfo(_dataStore, _taskFactory, _client.Id);
                    CurrentScreen = createCheckPointView;
                    MenuViewModel.CurrentScreen = WindowType.CheckPoints;
                    MenuViewModel.SetSaveCommand(new RelayCommand(() => createCheckPointView.Save()));
                    MenuViewModel.ShowSaveCommand = true;
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            break;
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

    public void Receive(EditingTransactionMessage message)
    {
        _enableEditingKeyWatcher = message.Value;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
        _dataStore.Dispose();
    }

    private async void DeleteClient(object? sender, EventArgs e)
    {
        if (_client is null)
        {
            return;
        }
        try
        {
            MessageDialogResult result = await this.ShowMessageAsync("Delete This Client", "Deleting this client will also delete all related accounts and information. Are you sure?", MessageDialogStyle.AffirmativeAndNegative, new MetroDialogSettings { AffirmativeButtonText = "OK", AnimateHide = true, AnimateShow = true, ColorScheme = MetroDialogColorScheme.Theme, DefaultButtonFocus = MessageDialogResult.Negative, NegativeButtonText = "Cancel" });
            if (result == MessageDialogResult.Negative)
            {
                return;
            }
            await _dataStore.DeleteClientAsync(_client.Id);
            if (MenuViewModel.ClientExists)
            {
                Receive(new ChangeScreenMessage(WindowType.ClientList));
                return;
            }
            Receive(new ChangeScreenMessage(WindowType.CreateClient));
        }
        catch (Exception)
        {
            return;
        }
    }

    private async void DeleteBusiness(object? sender, EventArgs e)
    {
        try
        {
            MessageDialogResult result = await this.ShowMessageAsync("Delete Business", "Are you sure you want to delete the business? This will delete the database and require setting up a new one.", MessageDialogStyle.AffirmativeAndNegative);
            if (result != MessageDialogResult.Affirmative)
            {
                return;
            }
            _dataStore.Dispose();
            string dbLocation = _dataStore.GetDbLocation();
            File.Delete(dbLocation);
            _dataStore.ClearRegistry();
            _dataStore = new DataStore();
            _serviceCollection.Replace(new ServiceDescriptor(typeof(IDataStore), _dataStore));
            Receive(new ChangeScreenMessage(WindowType.SetupDatabase));
        }
        catch (Exception)
        {
            return;
        }
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

    private void MainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_enableEditingKeyWatcher && !_enableTransactionKeyWatcher)
        {
            return;
        }

        if (!TransactionKeys.Contains(e.Key) && !EditingKeys.Contains(e.Key))
        {
            return;
        }

        if ((_enableEditingKeyWatcher && EditingKeys.Contains(e.Key)) || (_enableTransactionKeyWatcher && TransactionKeys.Contains(e.Key)))
        {
            e.Handled = true;
            WeakReferenceMessenger.Default.Send(new KeyPressedMessage(e.Key));
        }
    }
}