using System.Windows.Controls;
using Accounting101.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Accounting101.Controls
{
    [ObservableObject]
    public partial class AccountHeaderControl : UserControl
    {
        public decimal CurrentBalance
        {
            get => _currentBalance;
            set
            {
                _currentBalance = value;
                OnPropertyChanged();
            }
        }

        public string CoAId { get; }

        public DateOnly Created { get; }

        public string DebitCredit { get; }

        public string AccountName { get; }

        public decimal StartBalance { get; }

        public BaseAccountTypes Type { get; }

        private decimal _currentBalance;

        public AccountHeaderControl(AccountWithInfoFlat a)
        {
            CurrentBalance = a.GetBalance();
            CoAId = a.CoAId;
            Created = a.Created;
            DebitCredit = a.IsDebitAccount ? "Debit Account" : "Credit Account";
            AccountName = a.Name;
            StartBalance = a.GetStartBalance();
            Type = a.Type;
            DataContext = this;
            InitializeComponent();
        }

        public void UpdateBalance(decimal balance)
        {
            CurrentBalance = balance;
        }
    }
}