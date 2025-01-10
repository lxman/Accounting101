using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Accounting101.Messages
{
    public sealed class UpdateTransactionLayoutMessage(int? value) : ValueChangedMessage<int?>(value);
}