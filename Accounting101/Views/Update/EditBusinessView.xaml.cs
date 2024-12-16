using System.Windows.Controls;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update
{
    public partial class EditBusinessView : UserControl
    {
        public EditBusinessView(IDataStore dataStore, JoinableTaskFactory taskFactory)
        {
            DataContext = new EditBusinessViewModel(dataStore, taskFactory);
            InitializeComponent();
        }
    }
}