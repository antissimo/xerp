using System.Diagnostics.CodeAnalysis;

namespace Xerp.Application.Common;

/// <summary>The outcome of an Application operation: a value or an <see cref="AppError"/>.</summary>
public sealed class Result<T> where T : notnull
{
    private Result(T? value, AppError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public AppError? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(AppError error) => new(default, error);
}
