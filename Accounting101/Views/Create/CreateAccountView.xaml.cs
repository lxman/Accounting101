using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    /// <summary>
    /// Interaction logic for CreateAccountView.xaml
    /// </summary>
    public partial class CreateAccountView : UserControl
    {
        public CreateAccountView(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            DataContext = new CreateAccountViewModel(dataStore, taskFactory);
            InitializeComponent();
        }
    }
}
