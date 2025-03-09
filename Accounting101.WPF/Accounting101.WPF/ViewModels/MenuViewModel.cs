using System.Windows;
using System.Windows.Input;
using Accounting101.WPF.Messages;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

// ReSharper disable RedundantCatchClause
// ReSharper disable AsyncVoidMethod
// ReSharper disable SwitchStatementMissingSomeEnumCasesNoDefault
// ReSharper disable UnusedMember.Local
#pragma warning disable VSTHRD100
#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.ViewModels;

public class MenuViewModel : BaseViewModel
{
    public event EventHandler? DeleteClient;

    public event EventHandler? DeleteBusiness;

    public WindowType CurrentScreen
    {
        private get => _currentScreen;
        set
        {
            _currentScreen = value;
            SetMenus();
        }
    }

    public bool ShowFileSeparator => ShowNewMenu || ShowDeleteMenu || ShowSaveCommand;

    public bool ShowNewMenu => ShowNewClientCommand || ShowNewAccountCommand;

    public ICommand NewClientCommand { get; }

    public bool ShowNewClientCommand
    {
        get => _showNewClientCommand;
        set
        {
            SetField(ref _showNewClientCommand, value);
            OnPropertyChanged(nameof(ShowNewMenu));
        }
    }

    public ICommand NewAccountCommand { get; }

    public bool ShowNewAccountCommand
    {
        get => _showNewAccountCommand;
        set
        {
            SetField(ref _showNewAccountCommand, value);
            OnPropertyChanged(nameof(ShowNewMenu));
        }
    }

    public bool ShowDeleteMenu => ShowDeleteBusinessCommand || ShowDeleteClientCommand;

    public ICommand DeleteBusinessCommand { get; }

    public bool ShowDeleteBusinessCommand
    {
        get => _showDeleteBusinessCommand;
        set
        {
            SetField(ref _showDeleteBusinessCommand, value);
            OnPropertyChanged(nameof(ShowDeleteMenu));
        }
    }

    public ICommand DeleteClientCommand { get; }

    public bool ShowDeleteClientCommand
    {
        get => _showDeleteClientCommand;
        set
        {
            SetField(ref _showDeleteClientCommand, value);
            OnPropertyChanged(nameof(ShowDeleteMenu));
        }
    }

    public ICommand SaveCommand { get; private set; }

    public bool ShowSaveCommand
    {
        get => _showSaveCommand;
        set => SetField(ref _showSaveCommand, value);
    }

    public ICommand ExitCommand { get; }

    public bool ShowEditMenu => ShowEditBusinessCommand || ShowEditClientCommand || ShowEditAccountCommand || ShowEditCheckPointCommand;

    public ICommand EditBusinessCommand { get; }

    public bool ShowEditBusinessCommand
    {
        get => _showEditBusinessCommand;
        set
        {
            SetField(ref _showEditBusinessCommand, value);
            OnPropertyChanged(nameof(ShowEditMenu));
        }
    }

    public ICommand EditClientCommand { get; }

    public bool ShowEditClientCommand
    {
        get => _showEditClientCommand;
        set
        {
            SetField(ref _showEditClientCommand, value);
            OnPropertyChanged(nameof(ShowEditMenu));
        }
    }

    public ICommand EditAccountCommand { get; }

    public bool ShowEditAccountCommand
    {
        get => _showEditAccountCommand;
        set
        {
            SetField(ref _showEditAccountCommand, value);
            OnPropertyChanged(nameof(ShowEditMenu));
        }
    }

    public ICommand EditCheckPointCommand { get; }

    public bool ShowEditCheckPointCommand
    {
        get => _showEditCheckPointCommand;
        set
        {
            SetField(ref _showEditCheckPointCommand, value);
            OnPropertyChanged(nameof(ShowEditMenu));
        }
    }

    public bool ShowReportsMenu => ShowReportsBalanceSheetCommand || ShowReportsProfitAndLossCommand;

    public ICommand ReportsBalanceSheetCommand { get; }

    public bool ShowReportsBalanceSheetCommand
    {
        get => _showReportsBalanceSheetCommand;
        set
        {
            SetField(ref _showReportsBalanceSheetCommand, value);
            OnPropertyChanged(nameof(ShowReportsMenu));
        }
    }

    public ICommand ReportsProfitAndLossCommand { get; }

    public bool ShowReportsProfitAndLossCommand
    {
        get => _showReportsProfitAndLossCommand;
        set
        {
            SetField(ref _showReportsProfitAndLossCommand, value);
            OnPropertyChanged(nameof(ShowReportsMenu));
        }
    }

    public bool ShowClientListMenu => ShowClientListCommand;

    public ICommand ClientListCommand { get; }

    public ICommand ChangeThemeCommand { get; }

    public bool ShowChangeThemeCommand
    {
        get => _showChangeThemeCommand;
        set => SetField(ref _showChangeThemeCommand, value);
    }

    public bool ShowClientListCommand
    {
        get => _showClientListCommand;
        set
        {
            SetField(ref _showClientListCommand, value);
            OnPropertyChanged(nameof(ShowClientListMenu));
        }
    }

    public bool BusinessExists
    {
        private get => _businessExists;
        set
        {
            _businessExists = value;
            SetMenus();
        }
    }

    public bool ClientExists
    {
        get => _clientExists;
        set
        {
            _clientExists = value;
            SetMenus();
        }
    }

    public bool AccountExists
    {
        private get => _accountExists;
        set
        {
            _accountExists = value;
            SetMenus();
        }
    }

    private bool _businessExists;
    private bool _clientExists;
    private bool _accountExists;
    private bool _showNewClientCommand;
    private bool _showNewAccountCommand;
    private bool _showDeleteBusinessCommand;
    private bool _showDeleteClientCommand;
    private bool _showSaveCommand;
    private bool _showEditBusinessCommand;
    private bool _showEditClientCommand;
    private bool _showEditAccountCommand;
    private bool _showReportsBalanceSheetCommand;
    private bool _showReportsProfitAndLossCommand;
    private bool _showClientListCommand;
    private bool _showChangeThemeCommand;
    private bool _showEditCheckPointCommand;
    private WindowType _currentScreen;

    //private bool _showReportsIncomeStatementCommand;
    //private bool _showReportsTrialBalanceCommand;
    //private bool _showReportsGeneralLedgerCommand;
    //private bool _showReportsAccountsReceivableCommand;
    //private bool _showReportsAccountsPayableCommand;
    //private bool _showReportsCashFlowStatementCommand;
    //private bool _showReportsBudgetVsActualCommand;
    //private bool _showReportsTaxSummaryCommand;
    //private bool _showReportsTaxDetailCommand;
    //private bool _showReportsCustomReportCommand;
    private readonly IDataStore _dataStore;

    private readonly JoinableTaskFactory _taskFactory;

    public MenuViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory)
    {
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        _dataStore.StoreChanged += StoreChanged;
        ClientListCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ClientList)));
        NewClientCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.CreateClient)));
        NewAccountCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.CreateAccount)));
        EditBusinessCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.EditBusiness)));
        EditClientCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.EditClient)));
        EditCheckPointCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.CheckPoints)));
        ChangeThemeCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.UpdateTheme)));
        EditAccountCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new EditAccountMessage(true)));
        ReportsBalanceSheetCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.BalanceSheet)));
        ReportsProfitAndLossCommand = new RelayCommand(() => WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ProfitAndLoss)));
        DeleteClientCommand = new RelayCommand(() => DeleteClient?.Invoke(this, EventArgs.Empty));
        DeleteBusinessCommand = new RelayCommand(() => DeleteBusiness?.Invoke(this, EventArgs.Empty));
        ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
    }

    private void SetMenus()
    {
        ShowChangeThemeCommand = CurrentScreen != WindowType.GetPassword && CurrentScreen != WindowType.UpdateTheme;
        ShowClientListCommand = (CurrentScreen == WindowType.CreateClient && _clientExists)
                                || CurrentScreen == WindowType.CreateAccount
                                || (CurrentScreen == WindowType.EditBusiness && _clientExists)
                                || CurrentScreen == WindowType.EditClient
                                || CurrentScreen == WindowType.EditAccount
                                || CurrentScreen == WindowType.BalanceSheet
                                || CurrentScreen == WindowType.ProfitAndLoss
                                || CurrentScreen == WindowType.UpdateTheme
                                || CurrentScreen == WindowType.CheckPoints;
        ShowNewClientCommand = (CurrentScreen == WindowType.EditBusiness && _clientExists)
                               || CurrentScreen == WindowType.CreateAccount
                               || CurrentScreen == WindowType.ClientList
                               || CurrentScreen == WindowType.ClientAccountList
                               || CurrentScreen == WindowType.EditAccount
                               || CurrentScreen == WindowType.BalanceSheet
                               || CurrentScreen == WindowType.ProfitAndLoss;
        ShowNewAccountCommand = CurrentScreen is WindowType.ClientAccountList
            or WindowType.EditClient
            or WindowType.EditAccount
            or WindowType.BalanceSheet
            or WindowType.ProfitAndLoss;
        ShowDeleteBusinessCommand = CurrentScreen is WindowType.CreateClient
            or WindowType.CreateAccount
            or WindowType.ClientList
            or WindowType.ClientAccountList
            or WindowType.EditBusiness
            or WindowType.EditClient
            or WindowType.EditAccount
            or WindowType.BalanceSheet
            or WindowType.ProfitAndLoss;
        ShowDeleteClientCommand = CurrentScreen is WindowType.CreateAccount
            or WindowType.ClientAccountList
            or WindowType.EditClient
            or WindowType.EditAccount
            or WindowType.BalanceSheet
            or WindowType.ProfitAndLoss;
        ShowEditBusinessCommand = CurrentScreen is WindowType.CreateClient
            or WindowType.CreateAccount
            or WindowType.ClientList
            or WindowType.ClientAccountList
            or WindowType.EditClient
            or WindowType.EditAccount
            or WindowType.BalanceSheet
            or WindowType.ProfitAndLoss;
        ShowEditClientCommand = CurrentScreen is WindowType.CreateAccount
            or WindowType.ClientAccountList
            or WindowType.EditAccount
            or WindowType.BalanceSheet
            or WindowType.ProfitAndLoss;
        ShowEditCheckPointCommand = ShowEditClientCommand;
        ShowReportsBalanceSheetCommand = CurrentScreen is WindowType.CreateAccount
            or WindowType.ClientAccountList
            or WindowType.EditClient
            or WindowType.EditAccount
            or WindowType.ProfitAndLoss;
        ShowReportsProfitAndLossCommand = CurrentScreen is WindowType.CreateAccount
            or WindowType.ClientAccountList
            or WindowType.EditClient
            or WindowType.EditAccount
            or WindowType.BalanceSheet;
    }

    public void SetSaveCommand(RelayCommand cmd)
    {
        SaveCommand = cmd;
        OnPropertyChanged(nameof(SaveCommand));
    }

    private void StoreChanged(object? sender, ChangeEventArgs e)
    {
        switch (e.ChangeType)
        {
            case ChangeType.Created when e.ChangedType == typeof(Business):
                BusinessExists = true;
                break;

            case ChangeType.Created when e.ChangedType == typeof(Client):
            case ChangeType.Created when e.ChangedType == typeof(Clients):
                ClientExists = true;
                break;

            case ChangeType.Created when e.ChangedType == typeof(Account):
            case ChangeType.Created when e.ChangedType == typeof(Accounts):
                AccountExists = true;
                break;

            case ChangeType.Created when e.ChangedType == typeof(Transaction):
                break;

            case ChangeType.Created:
                {
                    if (e.ChangedType == typeof(Transactions))
                    {
                    }

                    break;
                }
            case ChangeType.Deleted:
                {
                    if (e.ChangedType == typeof(Transactions))
                    {
                    }
                    else if (e.ChangedType == typeof(Transaction))
                    {
                    }
                    else if (e.ChangedType == typeof(Accounts))
                    {
                    }
                    else if (e.ChangedType == typeof(Account))
                    {
                    }
                    else if (e.ChangedType == typeof(Clients))
                    {
                    }
                    else if (e.ChangedType == typeof(Client))
                    {
                        ClientExists = _taskFactory.Run(() => _dataStore.ClientsExistAsync());
                    }

                    break;
                }
        }

        SetMenus();
    }
}