using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.WPF.Messages;

public sealed class SetEditAccountVisibleMessage(Guid? value) : ValueChangedMessage<Guid?>(value);