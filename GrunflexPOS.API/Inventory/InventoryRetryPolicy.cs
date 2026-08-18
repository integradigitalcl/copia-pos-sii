namespace GrunflexPOS.API.Inventory;

/// <summary>Reintentos seguros solo para operaciones idempotentes (serialization/deadlock/busy).</summary>
public sealed class InventoryRetryPolicy
{
    public const int DefaultMaxAttempts = 5;

    private readonly ILogger<InventoryRetryPolicy> _log;

    public InventoryRetryPolicy(ILogger<InventoryRetryPolicy> log) => _log = log;

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        bool isIdempotent,
        string operationName,
        CancellationToken ct,
        int maxAttempts = DefaultMaxAttempts)
    {
        if (!isIdempotent)
            return await action(ct);

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await action(ct);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var pos = DatabaseErrorTranslator.TryTranslate(ex);
                var code = pos?.ErrorCode ?? DatabaseErrorTranslator.Classify(ex);
                if (code == null || !DatabaseErrorTranslator.IsRetriable(code))
                    throw;

                var delay = ComputeBackoff(attempt);
                _log.LogWarning(ex,
                    "inventory.retry operation={Op} attempt={A}/{Max} code={Code} delayMs={Delay}",
                    operationName, attempt, maxAttempts, code, delay.TotalMilliseconds);

                await Task.Delay(delay, ct);
            }
        }
    }

    public static TimeSpan ComputeBackoff(int attempt)
    {
        var baseMs = Math.Min(2000, 50 * (1 << Math.Min(attempt, 6)));
        var jitter = Random.Shared.Next(0, Math.Max(1, baseMs / 4));
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }
}
