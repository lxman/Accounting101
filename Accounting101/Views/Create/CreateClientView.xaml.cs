using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;

namespace Accounting101.Views.Create
{
    /// <summary>
    /// Interaction logic for CreateClientView.xaml
    /// </summary>
    public partial class CreateClientView : UserControl
    {
        public CreateClientView(IDataStore dataStore)
        {
            DataContext = new CreateClientViewModel(dataStore);
            InitializeComponent();
        }
    }
}
