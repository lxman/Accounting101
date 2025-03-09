using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.WPF.Messages;

public sealed class CreateDatabaseMessage(bool value) : ValueChangedMessage<bool>(value);