using System.Windows;
using Accounting101.ViewModels;
using DataAccess.Services.Interfaces;
using MahApps.Metro.Controls;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101
{
    public partial class MainWindow : MetroWindow
    {
        public static readonly DependencyProperty CurrentScreenProperty = DependencyProperty.Register(
            nameof(CurrentScreen), typeof(object), typeof(MainWindow), new PropertyMetadata(default(object)));

        public object CurrentScreen
        {
            get => GetValue(CurrentScreenProperty);
            set => SetValue(CurrentScreenProperty, value);
        }

        private readonly JoinableTaskFactory _taskFactory;
        private readonly IDataStore _dataStore;

        public MainWindow(IDataStore dataStore, MainWindowViewModel vm)
        {
            _taskFactory = new JoinableTaskFactory(new JoinableTaskCollection(new JoinableTaskContext()));
            _dataStore = dataStore;
            DataContext = vm;
            InitializeComponent();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Application.Current.Shutdown();
        }
    }
}