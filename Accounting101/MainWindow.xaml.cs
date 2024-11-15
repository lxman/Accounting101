using System.Windows;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;

namespace Accounting101
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IDataStore _dataStore;

        public MainWindow(IDataStore store)
        {
            _dataStore = store;
            InitializeComponent();
            DataContext = new MainWindowViewModel(_dataStore);
        }
    }
}