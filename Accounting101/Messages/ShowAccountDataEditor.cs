using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages
{
    public sealed class ShowAccountDataEditor(bool value) : ValueChangedMessage<bool>(value);
}
