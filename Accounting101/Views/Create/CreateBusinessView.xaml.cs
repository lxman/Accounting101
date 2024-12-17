using System.Windows.Controls;
using Accounting101.ViewModels.Create;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class CreateBusinessView : UserControl
    {
        public CreateBusinessView(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            DataContext = new CreateBusinessViewModel(dataStore, taskFactory);
            InitializeComponent();
        }
    }
}