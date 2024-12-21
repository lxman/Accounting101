using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages
{
    public sealed class BubbledScrollEventMessage(MouseWheelEventArgs value)
        : ValueChangedMessage<MouseWheelEventArgs>(value);
}