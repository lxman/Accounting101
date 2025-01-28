using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages;

public sealed class ChangeScreenMessage(WindowType value) : ValueChangedMessage<WindowType>(value);