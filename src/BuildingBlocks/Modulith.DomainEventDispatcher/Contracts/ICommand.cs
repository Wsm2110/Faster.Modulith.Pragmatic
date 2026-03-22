using System;
using System.Collections.Generic;
using System.Text;

namespace Modulith.DomainEventDispatcher.Contracts;

/// <summary>
/// Marker interface for a void command — represents intent to change state
/// with no return value. Handled by ICommandHandler&lt;TCommand&gt;.
/// </summary>
public interface ICommand
{
}

/// <summary>
/// Marker interface for a command that returns a result of type
/// <typeparamref name="TResult"/>. Handled by ICommandHandler&lt;TCommand, TResult&gt;.
/// </summary>
/// <typeparam name="TResult">The type returned after the command is handled.</typeparam>
public interface ICommand<TResult>
{
}

/// <summary>
/// Defines a handler for a void command — fire and forget, no return value.
/// One command must have exactly one handler registered in the DI container.
/// </summary>
/// <typeparam name="TCommand">The command type to handle.</typeparam>
public interface ICommandHandler<in TCommand> where TCommand : ICommand
{
    /// <summary>
    /// Handles the command asynchronously.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="ct">The cancellation token.</param>
    ValueTask Handle(TCommand command, CancellationToken ct = default);
}
