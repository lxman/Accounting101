using CommunityToolkit.Mvvm.Messaging.Messages;
using DataAccess.Models;

namespace Accounting101.Messages
{
    public sealed class AddTransactionMessage(Transaction value) : ValueChangedMessage<Transaction>(value);
}
