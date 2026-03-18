using Modulith.DomainEventDispatcher.Contracts;
using Modulith.WebApi.Modules.Replication.Contracts;
using Modulith.WebApi.Modules.Storage.Contracts;

namespace Modulith.WebApi.Modules.Replication.Application.EventHandlers;

/// <summary>
/// Demo Asynchronous cross module communication
/// </summary>
public class FriendlyTrackStoredEventHandler(INetworkReplicationClient networkReplicationClient) : IEventHandler<FriendlyTrackStoredEvent>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="domainEvent"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task Handle(FriendlyTrackStoredEvent domainEvent, CancellationToken cancellationToken)
    {
        // Demo purposes
        await networkReplicationClient.TransmitTrackAsync(null);      
    }
}
