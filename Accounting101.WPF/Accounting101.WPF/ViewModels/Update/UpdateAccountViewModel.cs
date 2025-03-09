using System.Collections.ObjectModel;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.ViewModels.Update;

public class UpdateAccountViewModel : BaseViewModel
{
    private readonly ObservableCollection<BaseAccountTypes> _accountTypes =
    [
        BaseAccountTypes.Asset,
        BaseAccountTypes.Liability,
        BaseAccountTypes.Equity,
        BaseAccountTypes.Revenue,
        BaseAccountTypes.Expense,
        BaseAccountTypes.Earnings
    ];

    public ReadOnlyObservableCollection<BaseAccountTypes> AccountTypes => new(_accountTypes);

    public AccountWithInfo? Account
    {
        get => _account;
        set
        {
            _account = value;
            OnPropertyChanged();
        }
    }

    private AccountWithInfo? _account;

    public void SetAccount(AccountWithInfo account)
    {
        Account = account;
        OnPropertyChanged(nameof(Account.StartBalance));
        OnPropertyChanged(nameof(Account.Info.CoAId));
        OnPropertyChanged(nameof(Account.Info.Name));
        OnPropertyChanged(nameof(Account.Type));
        OnPropertyChanged(nameof(Account.Created));
    }
}