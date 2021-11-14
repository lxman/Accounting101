using Accounting101.Common;
using DevExpress.Mvvm;
using System;

namespace Accounting101.Main.ViewModels
{
    [Serializable]
    public class NavigationItem : INavigationItem, ISupportState<NavigationItem>
    {
        public string Caption { get; set; }

        public NavigationItem()
        { }

        public NavigationItem(string caption)
        {
            Caption = caption;
        }

        #region Serialization

        NavigationItem ISupportState<NavigationItem>.SaveState()
        {
            return this;
        }

        void ISupportState<NavigationItem>.RestoreState(NavigationItem state)
        {
            Caption = state.Caption;
        }

        #endregion Serialization
    }
}