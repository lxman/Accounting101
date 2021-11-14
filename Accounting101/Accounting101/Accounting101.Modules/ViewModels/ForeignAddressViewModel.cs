using DataAccess.Models;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using DevExpress.Mvvm;
using DevExpress.Mvvm.POCO;

namespace Accounting101.Modules.ViewModels
{
    public class ForeignAddressViewModel : ViewModelBase
    {
        public ForeignAddress Address { get; set; }

        private static IDataStore DataStore;

        public static ForeignAddressViewModel Create(IDataStore store)
        {
            DataStore = store;
            DataStore.StoreChanged += StoreChanged;
            return ViewModelSource.Create(() => new ForeignAddressViewModel());
        }

        private static void StoreChanged(object? sender, ChangeEventArgs e)
        {
        }
    }
}