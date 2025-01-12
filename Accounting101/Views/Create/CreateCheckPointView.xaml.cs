using System.Windows.Controls;
using Accounting101.ViewModels.Create;

namespace Accounting101.Views.Create
{
    public partial class CreateCheckPointView : UserControl
    {
        private readonly CreateCheckPointViewModel _viewModel = new();

        public CreateCheckPointView()
        {
            DataContext = _viewModel;
            InitializeComponent();
        }
    }
}
