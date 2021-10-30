using Accounting101.Common;
using Accounting101.Main.Properties;
using Accounting101.Main.ViewModels;
using Accounting101.Main.Views;
using Accounting101.Modules.ViewModels;
using Accounting101.Modules.Views;
using Autofac;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using DevExpress.Mvvm;
using DevExpress.Mvvm.ModuleInjection;
using DevExpress.Mvvm.UI;
using DevExpress.Xpf.Core;
using System.ComponentModel;
using System.Windows;
using AppModules = Accounting101.Common.Modules;
using IContainer = Autofac.IContainer;
using Module = DevExpress.Mvvm.ModuleInjection.Module;

namespace Accounting101.Main
{
    public partial class App : Application
    {
        public App()
        {
            ApplicationThemeHelper.UpdateApplicationThemeName();
            SplashScreenManager.CreateThemed().ShowOnStartup();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Bootstrapper.Run();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ApplicationThemeHelper.SaveApplicationThemeName();
            base.OnExit(e);
        }
    }

    public class Bootstrapper
    {
        public static Bootstrapper Default { get; protected set; }

        public static void Run()
        {
            Default = new Bootstrapper();
            Default.RunCore();
        }

        protected Bootstrapper()
        {
        }

        private const string StateVersion = "1.0";
        private IContainer Container;

        public virtual void RunCore()
        {
            ConfigureTypeLocators();
            ConfigureServices();
            RegisterModules();
            if (!RestoreState())
                InjectModules();
            ConfigureNavigation();
            ShowMainWindow();
        }

        protected IModuleManager Manager => ModuleManager.DefaultManager;

        protected virtual void ConfigureTypeLocators()
        {
            var mainAssembly = typeof(MainViewModel).Assembly;
            var modulesAssembly = typeof(ModuleViewModel).Assembly;
            var assemblies = new[] { mainAssembly, modulesAssembly };
            ViewModelLocator.Default = new ViewModelLocator(assemblies);
            ViewLocator.Default = new ViewLocator(assemblies);
        }

        protected virtual void RegisterModules()
        {
            Manager.GetRegion(Regions.Documents).VisualSerializationMode = VisualSerializationMode.PerKey;
            Manager.Register(Regions.MainWindow, new Module(AppModules.Main, MainViewModel.Create, typeof(MainView)));
            Manager.Register(Regions.Navigation, new Module(AppModules.Module1, () => new NavigationItem("Module1")));
            Manager.Register(Regions.Navigation, new Module(AppModules.Module2, () => new NavigationItem("Module2")));
            Manager.Register(
                Regions.Documents,
                new Module(
                    AppModules.Module1,
                    () => ModuleViewModel.Create(
                        "Module1",
                        Container.Resolve<IDataStore>()),
                    typeof(ModuleView)));
            Manager.Register(
                Regions.Documents,
                new Module(
                    AppModules.Module2,
                    () => ModuleViewModel.Create(
                        "Module2",
                        Container.Resolve<IDataStore>()),
                    typeof(ModuleView)));
        }

        protected virtual void ConfigureServices()
        {
            var builder = new ContainerBuilder();
            builder.Register(c => new DataStore()).As<IDataStore>();
            Container = builder.Build();
        }

        protected virtual bool RestoreState()
        {
#if !DEBUG
            if (Settings.Default.StateVersion != StateVersion) return false;
            return Manager.Restore(Settings.Default.LogicalState, Settings.Default.VisualState);
#else
            return false;
#endif
        }

        protected virtual void InjectModules()
        {
            Manager.Inject(Regions.MainWindow, AppModules.Main);
            Manager.Inject(Regions.Navigation, AppModules.Module1);
            Manager.Inject(Regions.Navigation, AppModules.Module2);
        }

        protected virtual void ConfigureNavigation()
        {
            Manager.GetEvents(Regions.Navigation).Navigation += OnNavigation;
            Manager.GetEvents(Regions.Documents).Navigation += OnDocumentsNavigation;
        }

        protected virtual void ShowMainWindow()
        {
            Application.Current.MainWindow = new MainWindow();
            Application.Current.MainWindow.Show();
            Application.Current.MainWindow.Closing += OnClosing;
        }

        private void OnNavigation(object sender, NavigationEventArgs e)
        {
            if (e.NewViewModelKey == null) return;
            Manager.InjectOrNavigate(Regions.Documents, e.NewViewModelKey);
        }

        private void OnDocumentsNavigation(object sender, NavigationEventArgs e)
        {
            Manager.Navigate(Regions.Navigation, e.NewViewModelKey);
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            Manager.Save(out var logicalState, out var visualState);
            Settings.Default.StateVersion = StateVersion;
            Settings.Default.LogicalState = logicalState;
            Settings.Default.VisualState = visualState;
            Settings.Default.Save();
        }
    }
}