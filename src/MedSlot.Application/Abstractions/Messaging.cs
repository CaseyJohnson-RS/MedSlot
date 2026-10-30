using MedSlot.Domain.Common;

namespace MedSlot.Application.Abstractions;

// Свой минимальный набор вместо MediatR (ADR 0006): обработчики внедряются через DI напрямую,
// сквозная логика — декораторами при регистрации.

/// <summary>Команда меняет состояние системы.</summary>
public interface ICommand<TResponse>;

/// <summary>Запрос только читает данные.</summary>
public interface IQuery<TResponse>;

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Результат команды, которой нечего вернуть.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}
