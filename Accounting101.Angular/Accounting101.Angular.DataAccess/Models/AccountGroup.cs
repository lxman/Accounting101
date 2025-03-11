using System.Collections.Generic;

namespace Accounting101.Angular.DataAccess.Models
{
    class AccountGroup
    {
        public List<AccountGroup> Groups { get; set; } = [];

        public List<Account> Accounts { get; set; } = [];
    }
}
