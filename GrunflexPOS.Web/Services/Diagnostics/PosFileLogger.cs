namespace GrunflexPOS.Web.Services.Diagnostics;

public sealed class PosFileLoggerProvider(string logDirectory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new PosFileLogger(logDirectory, categoryName);

    public void Dispose()
    {
    }
}

public sealed class PosFileLogger(string logDirectory, string categoryName) : ILogger
{
    private static readonly object WriteGate = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
            return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {categoryName}: {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        try
        {
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, $"web-{DateTime.Now:yyyyMMdd}.log");
            lock (WriteGate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // El POS no debe fallar si no puede escribir el log.
        }
    }
}
