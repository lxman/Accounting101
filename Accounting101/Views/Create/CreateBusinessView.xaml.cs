using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.Create
{
    /// <summary>
    /// Interaction logic for CreateBusinessView.xaml
    /// </summary>
    public partial class CreateBusinessView : UserControl
    {
        public CreateBusinessView(IDataStore dataStore)
        {
            DataContext = new CreateBusinessViewModel(dataStore);
            InitializeComponent();
        }
    }
}
