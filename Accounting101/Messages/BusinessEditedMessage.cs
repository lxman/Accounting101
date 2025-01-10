using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages
{
    public sealed class BusinessEditedMessage(bool? value) : ValueChangedMessage<bool?>(value);
}