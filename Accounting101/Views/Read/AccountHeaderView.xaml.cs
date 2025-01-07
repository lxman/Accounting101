using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DataAccess.Models;

namespace Accounting101.Views.Read
{
    [ObservableObject]
    public partial class AccountHeaderView : UserControl
    {
        public string Created { get; private set; } = string.Empty;

        public string AccountName { get; private set; } = string.Empty;

        public string CoAId { get; private set; } = string.Empty;

        public decimal StartBalance { get; private set; }

        public string Type { get; private set; } = string.Empty;

        public string DebitCredit { get; private set; } = string.Empty;

        public AccountHeaderView()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetInfo(AccountWithInfo acct)
        {
            Created = acct.Created.ToString("MM/dd/yyyy");
            AccountName = acct.Info.Name;
            CoAId = acct.Info.CoAId;
            StartBalance = acct.StartBalance;
            Type = acct.Type.ToString();
            DebitCredit = acct.IsDebitAccount ? "Debit account" : "Credit account";
            OnPropertyChanged(nameof(Created));
            OnPropertyChanged(nameof(AccountName));
            OnPropertyChanged(nameof(CoAId));
            OnPropertyChanged(nameof(StartBalance));
            OnPropertyChanged(nameof(Type));
            OnPropertyChanged(nameof(DebitCredit));
        }
    }
}
