using Accounting101.ViewModels.Create;
using DataAccess.Models;

namespace Accounting101.Views.Create
{
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
}