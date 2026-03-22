using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Modulith.DomainEventDispatcher.Contracts;

/// <summary>
/// Defines the category of the error to drive HTTP status codes.
/// </summary>
public enum ErrorType
{
    /// <summary>
    /// Represents a general failure, typically resulting in a 400 Bad Request.
    /// </summary>
    Failure,

    /// <summary>
    /// Represents a validation failure, typically resulting in a 400 Bad Request.
    /// </summary>
    Validation,

    /// <summary>
    /// Represents a resource that was not found, typically resulting in a 404 Not Found.
    /// </summary>
    NotFound,

    /// <summary>
    /// Represents a state conflict, typically resulting in a 409 Conflict.
    /// </summary>
    Conflict,

    /// <summary>
    /// Represents an unauthenticated access attempt, typically resulting in a 401 Unauthorized.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// Represents an unauthorized access attempt for an authenticated user, typically resulting in a 403 Forbidden.
    /// </summary>
    Forbidden
}

/// <summary>
/// Represents the outcome of an operation without a return value.
/// </summary>
public readonly struct Result : IEquatable<Result>
{
    /// <summary>
    /// Gets a value indicating whether the operation was successful.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the error message if the operation failed.
    /// </summary>
    public string Error { get; }

    /// <summary>
    /// Gets the category of the error.
    /// </summary>
    public ErrorType ErrorType { get; }

    /// <summary>
    /// Gets a value indicating whether the operation failed.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    private Result(bool isSuccess, string error, ErrorType errorType)
    {
        IsSuccess = isSuccess;
        Error = error ?? string.Empty;
        ErrorType = errorType;
    }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static Result Success => new Result(true, string.Empty, ErrorType.Failure);

    /// <summary>
    /// Creates a failed result with a specific error message and type.
    /// </summary>
    /// <param name="error">The error message.</param>
    /// <param name="errorType">The type of error.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when error is null or whitespace.</exception>
    public static Result Failure(string error, ErrorType errorType = ErrorType.Failure)
    {
        if (string.IsNullOrWhiteSpace(error))
            throw new ArgumentException("Error message must not be empty", nameof(error));
        return new Result(false, error, errorType);
    }

    /// <summary>
    /// Creates a result representing a 404 Not Found error.
    /// </summary>
    public static Result NotFound(string error = "Resource not found.") => Failure(error, ErrorType.NotFound);

    /// <summary>
    /// Creates a result representing a 409 Conflict error.
    /// </summary>
    public static Result Conflict(string error = "Resource conflict occurred.") => Failure(error, ErrorType.Conflict);

    /// <summary>
    /// Creates a result representing a 401 Unauthorized error.
    /// </summary>
    public static Result Unauthorized(string error = "Unauthorized access.") => Failure(error, ErrorType.Unauthorized);

    /// <summary>
    /// Creates a result representing a 403 Forbidden error.
    /// </summary>
    public static Result Forbidden(string error = "Access forbidden.") => Failure(error, ErrorType.Forbidden);

    /// <summary>
    /// Creates a result representing a validation error.
    /// </summary>
    public static Result Validation(string error) => Failure(error, ErrorType.Validation);

    /// <summary>
    /// Executes an action based on the result outcome.
    /// </summary>
    /// <param name="success">Action to execute if successful.</param>
    /// <param name="failure">Action to execute with the error message if failed.</param>
    public void Match(Action success, Action<string> failure)
    {
        if (IsSuccess) success();
        else failure(Error);
    }

    /// <summary>
    /// returns a value based on the result outcome.
    /// </summary>
    /// <typeparam name="TResult">The type of the return value.</typeparam>
    /// <param name="success">Function to execute if successful.</param>
    /// <param name="failure">Function to execute with the error message if failed.</param>
    /// <returns>The result of the executed function.</returns>
    public TResult Match<TResult>(Func<TResult> success, Func<string, TResult> failure)
    {
        return IsSuccess ? success() : failure(Error);
    }

    /// <summary>
    /// Converts a non-generic Result to a generic Result&lt;T&gt;.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value to include if successful.</param>
    /// <returns>A generic <see cref="Result{T}"/>.</returns>
    public Result<T> To<T>(T value = default) =>
        IsSuccess ? Result<T>.Success(value) : Result<T>.Failure(Error, ErrorType);

    /// <summary>
    /// Executes the next function if the current result is successful, otherwise propagates the failure.
    /// </summary>
    /// <param name="next">The next function to execute.</param>
    /// <returns>The result of the next function or the current failure.</returns>
    public Result Bind(Func<Result> next)
    {
        return IsSuccess ? next() : Failure(Error, ErrorType);
    }

    /// <summary>
    /// Wraps an action that might throw an exception into a Result.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="errorMessagePrefix">Prefix for the error message if an exception occurs.</param>
    /// <returns>A successful result or a failure containing the exception message.</returns>
    public static Result Try(Action action, string errorMessagePrefix = "Operation failed")
    {
        try
        {
            action();
            return Success;
        }
        catch (Exception ex)
        {
            return Failure($"{errorMessagePrefix}: {ex.Message}");
        }
    }

    /// <summary>
    /// Wraps a function returning T that might throw an exception into a Result&lt;T&gt;.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="action">The function to execute.</param>
    /// <param name="errorMessagePrefix">Prefix for the error message if an exception occurs.</param>
    /// <returns>A successful result with the value or a failure containing the exception message.</returns>
    public static Result<T> Try<T>(Func<T> action, string errorMessagePrefix = "Operation failed")
    {
        try
        {
            return Result<T>.Success(action());
        }
        catch (Exception ex)
        {
            return Result<T>.Failure($"{errorMessagePrefix}: {ex.Message}");
        }
    }

    /// <summary>
    /// Combines multiple results into one. Returns Success if all are successful, otherwise aggregates the errors.
    /// </summary>
    /// <param name="results">The results to combine.</param>
    /// <returns>A combined result.</returns>
    public static Result Combine(params Result[] results)
    {
        var errors = new List<string>();
        foreach (var result in results)
        {
            if (result.IsFailure) errors.Add(result.Error);
        }

        return errors.Count > 0
            ? Failure(string.Join(", ", errors))
            : Success;
    }

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? "Success" : $"Failure ({ErrorType}): {Error}";

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is Result other && Equals(other);

    /// <inheritdoc />
    public bool Equals(Result other) => IsSuccess == other.IsSuccess && Error == other.Error && ErrorType == other.ErrorType;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsSuccess, Error, ErrorType);

    /// <summary>
    /// Equality operator for Result.
    /// </summary>
    public static bool operator ==(Result left, Result right) => left.Equals(right);

    /// <summary>
    /// Inequality operator for Result.
    /// </summary>
    public static bool operator !=(Result left, Result right) => !left.Equals(right);
}

/// <summary>
/// Represents the outcome of an operation with a resulting value.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
public readonly struct Result<T> : IEquatable<Result<T>>
{
    /// <summary>
    /// Gets a value indicating whether the operation was successful.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the error message if the operation failed.
    /// </summary>
    public string Error { get; }

    /// <summary>
    /// Gets the category of the error.
    /// </summary>
    public ErrorType ErrorType { get; }

    /// <summary>
    /// Gets the value produced by the operation.
    /// </summary>
    public T Value { get; }

    /// <summary>
    /// Gets a value indicating whether the operation failed.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    private Result(bool isSuccess, T value, string error, ErrorType errorType)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error ?? string.Empty;
        ErrorType = errorType;
    }

    /// <summary>
    /// Creates a successful result containing the specified value.
    /// </summary>
    public static Result<T> Success(T value) => new Result<T>(true, value, string.Empty, ErrorType.Failure);

    /// <summary>
    /// Creates a failed result with a specific error message and type.
    /// </summary>
    public static Result<T> Failure(string error, ErrorType errorType = ErrorType.Failure)
    {
        if (string.IsNullOrWhiteSpace(error))
            throw new ArgumentException("Error message must not be empty", nameof(error));
        return new Result<T>(false, default!, error, errorType);
    }

    /// <summary>
    /// Creates a result representing a 404 Not Found error.
    /// </summary>
    public static Result<T> NotFound(string error = "Resource not found.") => Failure(error, ErrorType.NotFound);

    /// <summary>
    /// Creates a result representing a 409 Conflict error.
    /// </summary>
    public static Result<T> Conflict(string error = "Resource conflict occurred.") => Failure(error, ErrorType.Conflict);

    /// <summary>
    /// Creates a result representing a 401 Unauthorized error.
    /// </summary>
    public static Result<T> Unauthorized(string error = "Unauthorized access.") => Failure(error, ErrorType.Unauthorized);

    /// <summary>
    /// Creates a result representing a 403 Forbidden error.
    /// </summary>
    public static Result<T> Forbidden(string error = "Access forbidden.") => Failure(error, ErrorType.Forbidden);

    /// <summary>
    /// Creates a result representing a validation error.
    /// </summary>
    public static Result<T> Validation(string error) => Failure(error, ErrorType.Validation);

    /// <summary>
    /// Implicitly converts a value of type T to a successful Result.
    /// </summary>
    public static implicit operator Result<T>(T value) => Success(value);

    /// <summary>
    /// Executes an action based on the result outcome.
    /// </summary>
    public void Match(Action<T> success, Action<string> failure)
    {
        if (IsSuccess) success(Value);
        else failure(Error);
    }

    /// <summary>
    /// Returns a value based on the result outcome.
    /// </summary>
    public TResult Match<TResult>(Func<T, TResult> success, Func<string, TResult> failure)
    {
        return IsSuccess ? success(Value) : failure(Error);
    }

    /// <summary>
    /// Converts the generic Result&lt;T&gt; to a non-generic Result.
    /// </summary>
    public Result ToResult() => IsSuccess ? Result.Success : Result.Failure(Error, ErrorType);

    /// <summary>
    /// Transforms the inner value using the mapper function if the result is successful.
    /// </summary>
    public Result<TOutput> Map<TOutput>(Func<T, TOutput> mapper)
    {
        return IsSuccess ? Result<TOutput>.Success(mapper(Value)) : Result<TOutput>.Failure(Error, ErrorType);
    }

    /// <summary>
    /// Chains another operation that returns a Result if the current result is successful.
    /// </summary>
    public Result<TOutput> Bind<TOutput>(Func<T, Result<TOutput>> next)
    {
        return IsSuccess ? next(Value) : Result<TOutput>.Failure(Error, ErrorType);
    }

    /// <summary>
    /// Ensures the value satisfies a predicate, otherwise returns a failure.
    /// </summary>
    public Result<T> Ensure(Func<T, bool> predicate, string errorMessage)
    {
        if (IsFailure) return this;
        return predicate(Value) ? this : Failure(errorMessage);
    }

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? $"Success: {Value}" : $"Failure ({ErrorType}): {Error}";

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is Result<T> other && Equals(other);

    /// <inheritdoc />
    public bool Equals(Result<T> other)
    {
        if (IsSuccess != other.IsSuccess) return false;
        if (!IsSuccess) return Error == other.Error && ErrorType == other.ErrorType;
        return EqualityComparer<T>.Default.Equals(Value, other.Value);
    }

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsSuccess, Value, Error, ErrorType);

    /// <summary>
    /// Equality operator for generic Result.
    /// </summary>
    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    /// <summary>
    /// Inequality operator for generic Result.
    /// </summary>
    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);
}

/// <summary>
/// Provides extension methods for Result types to support asynchronous flows and side-effects.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Executes a side-effect action if the result is successful.
    /// </summary>
    public static Result<T> Tap<T>(this Result<T> result, Action<T> action)
    {
        if (result.IsSuccess) action(result.Value);
        return result;
    }

    /// <summary>
    /// Asynchronously executes a side-effect action if the result is successful.
    /// </summary>
    public static async Task<Result<T>> TapAsync<T>(this Task<Result<T>> resultTask, Func<T, Task> action)
    {
        var result = await resultTask;
        if (result.IsSuccess) await action(result.Value);
        return result;
    }

    /// <summary>
    /// Asynchronously transforms the inner value if the result is successful.
    /// </summary>
    public static async Task<Result<U>> MapAsync<T, U>(this Task<Result<T>> resultTask, Func<T, Task<U>> mapper)
    {
        var result = await resultTask;
        if (result.IsFailure) return Result<U>.Failure(result.Error, result.ErrorType);

        var newValue = await mapper(result.Value);
        return Result<U>.Success(newValue);
    }

    /// <summary>
    /// Asynchronously chains another operation that returns a Result if the current result is successful.
    /// </summary>
    public static async Task<Result<U>> BindAsync<T, U>(this Task<Result<T>> resultTask, Func<T, Task<Result<U>>> next)
    {
        var result = await resultTask;
        if (result.IsFailure) return Result<U>.Failure(result.Error, result.ErrorType);

        return await next(result.Value);
    }

    /// <summary>
    /// Asynchronously matches the result outcome and returns a value.
    /// </summary>
    public static async Task<TOutput> MatchAsync<T, TOutput>(
        this Task<Result<T>> resultTask,
        Func<T, TOutput> onSuccess,
        Func<string, TOutput> onFailure)
    {
        var result = await resultTask;
        return result.Match(onSuccess, onFailure);
    }
}