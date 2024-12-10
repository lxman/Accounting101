using DataAccess.Models;

namespace Accounting101.Models
{
    public class AccountPickerLine(AccountWithInfo a)
    {
        public Guid Id => a.Id;

        public string CoAId { get; } = a.Info.CoAId;

        public string Name { get; } = a.Info.Name;

        public BaseAccountTypes Type { get; } = a.Type;
    }
}