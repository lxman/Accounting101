using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages
{
    public sealed class EditAccountMessage(bool value) : ValueChangedMessage<bool>(value);
}