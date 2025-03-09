using System.Windows.Input;
using Accounting101.WPF.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.Views.Read;

[ObservableObject]
public partial class AccountHeaderView
{
    public string Created { get; private set; } = string.Empty;

    public string AccountName { get; private set; } = string.Empty;

    public string CoAId { get; private set; } = string.Empty;

    public decimal StartBalance { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public string DebitCredit { get; private set; } = string.Empty;

    public string CheckPointActive
    {
        get => _checkPointActive;
        set
        {
            _checkPointActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCheckPointActive));
        }
    }

    public bool ShowCheckPointActive => !string.IsNullOrEmpty(CheckPointActive);

    private string _checkPointActive = string.Empty;

    public decimal CurrentBalance
    {
        get => _currentBalance;
        set
        {
            _currentBalance = value;
            OnPropertyChanged();
        }
    }

    private decimal _currentBalance;

    public AccountHeaderView()
    {
        DataContext = this;
        InitializeComponent();
    }

    public void SetInfo(AccountWithEverything acct)
    {
        Created = acct.Account.Created.ToString("MM/dd/yyyy");
        AccountName = acct.Info.Name;
        CoAId = acct.Info.CoAId;
        StartBalance = acct.Account.StartBalance;
        Type = acct.Account.Type.ToString();
        DebitCredit = acct.Account.IsDebitAccount ? "Debit account" : "Credit account";
        OnPropertyChanged(nameof(Created));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(CoAId));
        OnPropertyChanged(nameof(StartBalance));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(DebitCredit));
        if (acct.CheckPoint is not null)
        {
            CheckPointActive = $"Check point active: {acct.CheckPoint.Date.ToString("MM/dd/yyyy")}";
        }
    }

    private void AccountHeaderViewPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ClientAccountList));
    }
}