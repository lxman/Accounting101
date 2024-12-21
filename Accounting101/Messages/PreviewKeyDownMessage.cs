using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages
{
    public sealed class PreviewKeyDownMessage(Key value) : ValueChangedMessage<Key>(value);
}