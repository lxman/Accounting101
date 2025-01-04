using CommunityToolkit.Mvvm.Messaging.Messages;
using DataAccess.Models;

namespace Accounting101.Messages
{
    public sealed class FocusClientMessage(ClientWithInfo value) : ValueChangedMessage<ClientWithInfo>(value);
}
