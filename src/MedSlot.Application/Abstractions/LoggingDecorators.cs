using System.Diagnostics;
using MedSlot.Domain.Common;
using Microsoft.Extensions.Logging;

namespace MedSlot.Application.Abstractions;

// Логируются только имя операции, исход и длительность: в командах есть персональные данные.

internal sealed class LoggingCommandHandlerDecorator<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner,
    ILogger<LoggingCommandHandlerDecorator<TCommand, TResponse>> logger)
    : ICommandHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await inner.Handle(command, cancellationToken).ConfigureAwait(false);
        HandlerLog.Completed(logger, typeof(TCommand).Name, result, Stopwatch.GetElapsedTime(started));
        return result;
    }
}

internal sealed class LoggingQueryHandlerDecorator<TQuery, TResponse>(
    IQueryHandler<TQuery, TResponse> inner,
    ILogger<LoggingQueryHandlerDecorator<TQuery, TResponse>> logger)
    : IQueryHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    public async Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await inner.Handle(query, cancellationToken).ConfigureAwait(false);
        HandlerLog.Completed(logger, typeof(TQuery).Name, result, Stopwatch.GetElapsedTime(started));
        return result;
    }
}

internal static partial class HandlerLog
{
    public static void Completed(ILogger logger, string operation, Result result, TimeSpan elapsed)
    {
        if (result.IsSuccess)
        {
            Succeeded(logger, operation, elapsed.TotalMilliseconds);
        }
        else
        {
            Rejected(logger, operation, result.Error.Code, elapsed.TotalMilliseconds);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Operation} выполнена за {ElapsedMs:F1} мс")]
    private static partial void Succeeded(ILogger logger, string operation, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Operation} отклонена: {ErrorCode} ({ElapsedMs:F1} мс)")]
    private static partial void Rejected(ILogger logger, string operation, string errorCode, double elapsedMs);
}
