using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.WPF.Messages;

public sealed class KeyPressedMessage(Key value) : ValueChangedMessage<Key>(value);