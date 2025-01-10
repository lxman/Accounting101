using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages
{
    public sealed class CreateDatabaseMessage(bool value) : ValueChangedMessage<bool>(value);
}