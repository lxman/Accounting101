using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.WPF.Messages;

public sealed class EditingTransactionMessage(bool value) : ValueChangedMessage<bool>(value);