using Accounting101.Common;
using DataAccess.Services.Interfaces;
using DevExpress.Mvvm;
using DevExpress.Mvvm.POCO;
using System;

namespace Accounting101.Modules.ViewModels
{
    public class ClientsViewModel : IDocumentModule, ISupportState<ClientsViewModel.Info>
    {
        public string Caption { get; private set; }

        public virtual bool IsActive { get; set; }

        private static IDataStore DataStore;

        public static ClientsViewModel Create(string caption, IDataStore dataStore)
        {
            DataStore = dataStore;
            return ViewModelSource.Create(() => new ClientsViewModel()
            {
                Caption = caption,
            });
        }

        public ClientsViewModel()
        {
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
}