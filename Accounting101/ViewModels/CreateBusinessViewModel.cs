using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace Accounting101.ViewModels
{
    public class CreateBusinessViewModel
    {
        public bool ForeignCheckboxState
        {
            get => _foreignCheckboxState;
            set
            {
                ForeignCheckboxChangeState(value);
                _foreignCheckboxState = value;
            }
        }

        public Business Business
        {
            get => _business;
            set => _business = value;
        }

        private bool _foreignCheckboxState;
        private Business _business;

        public CreateBusinessViewModel(IDataStore dataStore)
        {
            _business = dataStore.GetBusiness();
            if (_business.Address is ForeignAddress)
            {
                _foreignCheckboxState = true;
            }
        }

        private void ForeignCheckboxChangeState(bool state)
        {
            if (state)
            {
                Business.Address = new ForeignAddress();
            }
            else
            {
                Business.Address = new UsAddress();
            }
        }
    }
}
