﻿using System.Windows.Controls;
using Accounting101.ViewModels.Single;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Create
{
    public partial class CreateUSAddressView : UserControl
    {
        public CreateUSAddressView(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid? addressId = null)
        {
            DataContext = new USAddressViewModel(dataStore, taskFactory, addressId);
            InitializeComponent();
        }
    }
}