using System;
using System.Collections.Generic;
using System.Text;

namespace Modulith.DomainEventDispatcher.Contracts;

/// <summary>
/// Defines a handler for a command that produces a result.
/// One command must have exactly one handler registered in the DI container.
/// </summary>
/// <typeparam name="TCommand">The command type to handle.</typeparam>
/// <typeparam name="TResult">The type returned after handling.</typeparam>
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    /// <summary>
    /// Handles the command asynchronously and returns a result.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> containing the result.</returns>
    ValueTask<TResult> Handle(TCommand command, CancellationToken ct = default);
}