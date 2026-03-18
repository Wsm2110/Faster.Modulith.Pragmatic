namespace Modulith.DomainEventDispatcher.Contracts;

/// <summary>
/// A marker interface representing a domain event.
/// </summary>
public interface IEvent
{
}

/// <summary>
/// Defines a handler that processes a specific event and returns a designated type.
/// </summary>
/// <typeparam name="TEvent">The type of the event.</typeparam>
/// <typeparam name="TResponse">The expected return type.</typeparam>
public interface IEventHandler<in TEvent> where TEvent : IEvent
{
    /// <summary>
    /// Handles the event asynchronously.
    /// </summary>
    /// <param name="domainEvent">The event data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task containing the processing response.</returns>
    Task Handle(TEvent domainEvent, CancellationToken cancellationToken);
}
