using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages
{
    public sealed class AccountActiveMessage(Guid value) : ValueChangedMessage<Guid>(value);
}
