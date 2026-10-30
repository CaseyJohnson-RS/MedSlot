namespace MedSlot.Domain.Common;

public class Result
{
    protected Result(DomainError error) => Error = error;

    public DomainError Error { get; }

    public bool IsSuccess => Error == DomainError.None;

    public bool IsFailure => !IsSuccess;

    public static Result Success() => new(DomainError.None);

    /// <summary>Нужен, когда <typeparamref name="T"/> — интерфейс: к ним неявные преобразования не применяются.</summary>
    public static Result<T> Success<T>(T value) => Result<T>.FromValue(value);

    public static Result Failure(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error == DomainError.None
            ? throw new ArgumentException("Неуспешный результат должен содержать ошибку.", nameof(error))
            : new Result(error);
    }

    public static implicit operator Result(DomainError error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value)
        : base(DomainError.None) => _value = value;

    private Result(DomainError error)
        : base(error)
    {
    }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Нет значения у неуспешного результата: {Error.Code}.");

    public static implicit operator Result<T>(T value) => new(value);

    internal static Result<T> FromValue(T value) => new(value);

    public static implicit operator Result<T>(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error == DomainError.None
            ? throw new ArgumentException("Неуспешный результат должен содержать ошибку.", nameof(error))
            : new Result<T>(error);
    }
}
