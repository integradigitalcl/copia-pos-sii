using System;

namespace GrunflexPOS2.Services.Multicaja.Sync;

/// <summary>Estado observable de la última sincronización de catálogo (UI / diagnóstico).</summary>
public static class SyncStatusService
{
    public static DateTime LastSuccessUtc { get; private set; } = DateTime.MinValue;
    public static string? LastError { get; private set; }
    public static bool IsRunning { get; private set; }
    public static SyncReason? LastReason { get; private set; }

    public static event EventHandler? Changed;

    public static void SetRunning(bool running)
    {
        IsRunning = running;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetSuccess(SyncReason reason, DateTime utc)
    {
        IsRunning = false;
        LastSuccessUtc = utc;
        LastError = null;
        LastReason = reason;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetFailure(SyncReason reason, string error)
    {
        IsRunning = false;
        LastError = error;
        LastReason = reason;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
