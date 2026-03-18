namespace Modulith.DomainEventDispatcher.Contracts;

public interface IEventDispatcher
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default) where TEvent : IEvent;
}