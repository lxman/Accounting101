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

        public DelegateCommand ChoosePath { get; private set; }

        IServiceContainer ISupportServices.ServiceContainer => ServiceContainer;

        protected IServiceContainer ServiceContainer
        {
            get { return ServiceContainerInternal ??= new ServiceContainer(this); }
        }

        private IServiceContainer ServiceContainerInternal;
        private static ISettingsStore SettingsStore;

        private ISaveFileDialogService FileDialogService => this.GetService<ISaveFileDialogService>();

        public static SettingsViewModel Create(
            string caption,
            ISettingsStore store)
        {
            SettingsStore = store;
            return ViewModelSource.Create(() => new SettingsViewModel()
            {
                Caption = caption,
            });
        }

        public SettingsViewModel()
        {
            ChoosePath = new DelegateCommand(SetPath);
        }

        private void SetPath()
        {
            if (FileDialogService.ShowDialog())
            {
                var tmp = FileDialogService.File;
            }
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