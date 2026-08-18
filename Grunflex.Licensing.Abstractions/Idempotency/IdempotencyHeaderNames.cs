namespace Grunflex.Idempotency;

/// <summary>Cabeceras HTTP estándar Grunflex para idempotencia multicaja (cliente ↔ API).</summary>
public static class IdempotencyHeaderNames
{
    public const string RequestId = "X-Grunflex-Request-Id";
    public const string Terminal = "X-Grunflex-Terminal";
    public const string CajaId = "X-Grunflex-Caja-Id";
    public const string PayloadHash = "X-Grunflex-Payload-Hash";
}
