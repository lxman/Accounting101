using CommunityToolkit.Mvvm.Messaging.Messages;
using DataAccess.WPF.Models;

namespace Accounting101.WPF.Messages;

public sealed class FocusClientMessage(ClientWithInfo value) : ValueChangedMessage<ClientWithInfo>(value);