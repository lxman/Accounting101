using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages
{
    public sealed class SaveMessage(WindowType value) : ValueChangedMessage<WindowType>(value);
}