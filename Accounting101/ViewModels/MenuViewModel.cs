using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services;
using DataAccess.Services.Interfaces;

#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class MenuViewModel : BaseViewModel
    {
        public event EventHandler? DeleteClient;

        public bool ShowNewMenu => ShowNewClientCommand || ShowNewAccountCommand;

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

        public bool ShowDeleteMenu => ShowDeleteBusinessCommand
                                      || ShowDeleteClientCommand;

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

        public ICommand SaveCommand
        {
            get => _saveCommand;
            set => SetField(ref _saveCommand, value);
        }

        public bool ShowSaveCommand
        {
            get => _showSaveCommand;
            set => SetField(ref _showSaveCommand, value);
        }

        public ICommand ExitCommand { get; }

        public bool ShowEditMenu => ShowEditBusinessCommand
                                    || ShowEditClientCommand
                                    || ShowEditAccountCommand;

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

        public bool ShowReportsMenu => ShowReportsBalanceSheetCommand
                                       || ShowReportsProfitAndLossCommand;

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

        public WindowType ActiveWindow { private get; set; }

        private ICommand _saveCommand;
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

        public MenuViewModel(IDataStore dataStore)
        {

            _dataStore = dataStore;
            _dataStore.StoreChanged += StoreChanged;
            ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
        }

        public void SetSaveCommand(RelayCommand cmd)
        {
            SaveCommand = cmd;
        }

        private void ChangeMenuState()
        {
            switch (BusinessExists)
            {
                case false:
                    ShowNewClientCommand = false;
                    ShowNewAccountCommand = false;
                    break;

                case true when !ClientExists && !AccountExists:
                    ShowNewClientCommand = true;
                    ShowDeleteBusinessCommand = true;
                    ShowDeleteClientCommand = false;
                    ShowNewAccountCommand = false;
                    ShowEditBusinessCommand = true;
                    break;

                case true when ClientExists && !AccountExists:
                    ShowDeleteBusinessCommand = true;
                    ShowDeleteClientCommand = true;
                    ShowNewClientCommand = true;
                    ShowNewAccountCommand = true;
                    ShowEditBusinessCommand = true;
                    break;

                case true when ClientExists && AccountExists:
                    ShowDeleteBusinessCommand = true;
                    ShowDeleteClientCommand = true;
                    ShowNewClientCommand = true;
                    ShowNewAccountCommand = true;
                    ShowEditBusinessCommand = true;
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
            }
            ChangeMenuState();
        }

        private void DeleteBusiness()
        {
            _dataStore.Dispose();
            string dbLocation = _dataStore.GetDbLocation();
            File.Delete(dbLocation);
            _dataStore.ClearRegistry();
            Application.Current.Shutdown(0);
        }
    }
}