using DevExpress.Mvvm.POCO;

namespace Accounting101.Main.ViewModels
{
    public class MainViewModel
    {
        public static MainViewModel Create()
        {
            return ViewModelSource.Create(() => new MainViewModel());
        }
    }
}