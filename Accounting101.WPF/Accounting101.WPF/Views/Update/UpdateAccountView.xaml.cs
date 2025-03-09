using Accounting101.WPF.ViewModels.Update;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.Views.Update;

public partial class UpdateAccountView
{
    public event EventHandler<AccountWithInfo?>? SaveChanges;

    private readonly UpdateAccountViewModel _viewModel = new();

    public UpdateAccountView()
    {
        DataContext = _viewModel;
        InitializeComponent();
    }

    public void SetAccount(AccountWithInfo account)
    {
        _viewModel.SetAccount(account);
    }

    public void SaveAccountChanges()
    {
        SaveChanges?.Invoke(this, _viewModel.Account);
    }
}