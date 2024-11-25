using System.Windows;
using System.Windows.Input;
using Accounting101.Commands;
using Accounting101.Views.Create;
using DataAccess.Services.Interfaces;
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels
{
    public class MainWindowViewModel : BaseViewModel
    {
        public object PageContent { get; }

        public ICommand NewCommand { get; }

        public ICommand SaveCommand { get; }

        public ICommand ExitCommand { get; }

        private readonly IDataStore _dataStore;

        public MainWindowViewModel(IDataStore dataStore)
        {
            _dataStore = dataStore;
            ExitCommand = new DelegateCommand(() =>
            {
                _dataStore.Dispose();
                Application.Current.Shutdown();
            });
            if (!BusinessCreated())
            {
                CreateBusinessView createBusinessView = new(_dataStore);
                CreateBusinessViewModel createBusinessViewModel = (CreateBusinessViewModel)createBusinessView.DataContext;
                PageContent = createBusinessView;
                SaveCommand = new DelegateCommand(() => createBusinessViewModel.Save());
            }
        }

        private bool BusinessCreated()
        {
            return _dataStore.GetBusiness() is not null;
        }
    }
}