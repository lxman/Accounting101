using System.Windows;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IDataStore, DataStore>();
            services.AddSingleton<MainWindow>();
            ServiceProvider serviceProvider = services.BuildServiceProvider();

            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow?.Show();
        }
    }
}
