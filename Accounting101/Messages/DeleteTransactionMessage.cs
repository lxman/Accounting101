using CommunityToolkit.Mvvm.Messaging.Messages;
using DataAccess.Models;

namespace Accounting101.Messages
{
    public sealed class DeleteTransactionMessage(Transaction value) : ValueChangedMessage<Transaction>(value);
}