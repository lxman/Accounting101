using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.WPF.Messages;

public sealed class ShowAccountTransactionEditor(ShowAccountTransactionMessage value) : ValueChangedMessage<ShowAccountTransactionMessage>(value);