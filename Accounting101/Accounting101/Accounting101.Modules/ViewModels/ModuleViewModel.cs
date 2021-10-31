using Accounting101.Common;
using DataAccess.Services.Interfaces;
using DevExpress.Mvvm;
using DevExpress.Mvvm.POCO;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Accounting101.Modules.ViewModels
{
    public class ModuleViewModel : IDocumentModule, ISupportState<ModuleViewModel.Info>
    {
        public string Caption { get; private set; }

        public virtual bool IsActive { get; set; }

        public ObservableCollection<DataItem> Items { get; private set; }

        private static IDataStore DataStore;

        public static ModuleViewModel Create(string caption, IDataStore dataStore)
        {
            DataStore = dataStore;
            return ViewModelSource.Create(() => new ModuleViewModel()
            {
                Caption = caption,
            });
        }

        protected ModuleViewModel()
        {
            Items = new ObservableCollection<DataItem>();
            Enumerable.Range(0, 100)
                .Select(x => new DataItem() { Id = x, Value = "Item #" + x })
                .ToList()
                .ForEach(x => Items.Add(x));
        }

        #region Serialization

        [Serializable]
        public class Info
        {
            public string Caption { get; set; }
        }

        Info ISupportState<Info>.SaveState()
        {
            return new Info()
            {
                Caption = Caption,
            };
        }

        void ISupportState<Info>.RestoreState(Info state)
        {
            Caption = state.Caption;
        }

        #endregion Serialization
    }

    public class DataItem
    {
        public int Id { get; set; }
        public string Value { get; set; }
    }
}