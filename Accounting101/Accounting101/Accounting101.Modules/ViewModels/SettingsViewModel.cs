using Accounting101.Common;
using DataAccess.Services.Interfaces;
using DevExpress.Mvvm;
using DevExpress.Mvvm.POCO;
using System;

namespace Accounting101.Modules.ViewModels
{
    public class SettingsViewModel : IDocumentModule, ISupportState<SettingsViewModel.Info>, ISupportServices
    {
        public string Caption { get; private set; }

        public virtual bool IsActive { get; set; }

        IServiceContainer ISupportServices.ServiceContainer => ServiceContainer;

        protected IServiceContainer ServiceContainer
        {
            get { return ServiceContainerInternal ??= new ServiceContainer(this); }
        }

        private IServiceContainer ServiceContainerInternal;
        private static IDataStore DataStore;

        public static SettingsViewModel Create(
            string caption,
            IDataStore store)
        {
            DataStore = store;
            return ViewModelSource.Create(() => new SettingsViewModel()
            {
                Caption = caption,
            });
        }

        public SettingsViewModel()
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