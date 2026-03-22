using Microsoft.Extensions.DependencyInjection;
using Modulith.DomainEventDispatcher.Contracts;

namespace Modulith.DomainEventDispatcher;

/// <summary>
/// Provides functionality to dispatch domain events to all registered event handlers using dependency injection.
/// </summary>
/// <remarks>The EventDispatcher is designed for high-performance, fire-and-forget event publishing scenarios. It
/// resolves all handlers for a given event type from the provided IServiceProvider and invokes them asynchronously.
/// This class does not return responses from handlers and is suitable for use in applications following the
/// domain-driven design (DDD) pattern. Thread safety and handler execution order are not guaranteed.</remarks>
public class EventDispatcher : IEventDispatcher
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventDispatcher"/> class.
    /// </summary>
    /// <param name="serviceProvider">The dependency injection provider.</param>
    public EventDispatcher(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Routes a domain event to all registered handlers with extreme performance.
    /// Events are fire-and-forget and do not yield a response payload.
    /// </summary>
    /// <typeparam name="TEvent">The specific event type.</typeparam>
    /// <param name="domainEvent">The event data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous publish operation.</returns>
    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default) where TEvent : IEvent
    {
        // DI resolution is highly optimized in modern .NET, but it does allocate an array.
        var handlers = _serviceProvider.GetServices<IEventHandler<TEvent>>();

        // Pre-allocate the list capacity to avoid dynamic resizing. 
        // 4 is a safe, low-memory default for most multi-subscriber scenarios.
        var tasks = new List<Task>(4);

        // Using a standard foreach eliminates the LINQ enumerator and closure allocations.
        foreach (var handler in handlers)
        {
            tasks.Add(handler.Handle(domainEvent, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }
}