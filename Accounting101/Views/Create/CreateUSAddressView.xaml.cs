using System.Windows.Controls;
using Accounting101.ViewModels.Create;
using DataAccess.Models;

namespace Accounting101.Views.Create
{
    public partial class CreateUSAddressView : UserControl
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
}