using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels
{
    public class MainWindowViewModel : BaseViewModel
    {
        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;

        public MainWindowViewModel(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            MenuViewModel menuViewModel)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
        }
    }
}