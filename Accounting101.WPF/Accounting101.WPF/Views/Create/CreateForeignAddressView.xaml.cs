using Accounting101.WPF.ViewModels.Create;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.Views.Create;

public partial class CreateForeignAddressView
{
    private readonly CreateForeignAddressViewModel _viewModel = new();

    public CreateForeignAddressView()
    {
        DataContext = _viewModel;
        InitializeComponent();
    }

    public ForeignAddress GetResult() => _viewModel.GetResult();
}