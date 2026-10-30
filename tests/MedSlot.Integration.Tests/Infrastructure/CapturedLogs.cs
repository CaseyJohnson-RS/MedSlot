using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MedSlot.Integration.Tests.Infrastructure;

/// <summary>
/// Провайдер логов, запоминающий всё, что пишут API и воркеры: текст сообщения, значения
/// структурированных параметров и исключения. Нужен для проверки, что персональные данные в логи не попадают.
/// </summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            // Значения параметров отдельно: параметр может не войти в шаблон сообщения, но уйти в структурированный лог.
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {properties} {exception}");
        }
    }
}
