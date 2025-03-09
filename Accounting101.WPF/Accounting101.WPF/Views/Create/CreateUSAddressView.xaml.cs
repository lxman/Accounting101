using Accounting101.WPF.ViewModels.Create;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.Views.Create;

public partial class CreateUSAddressView
{
    private readonly CreateUSAddressViewModel _viewModel = new();

    public CreateUSAddressView()
    {
        DataContext = _viewModel;
        InitializeComponent();
    }

    public void SetStates(List<string> states) => _viewModel.SetStates(states);

    public UsAddress GetResult() => _viewModel.GetResult();
}