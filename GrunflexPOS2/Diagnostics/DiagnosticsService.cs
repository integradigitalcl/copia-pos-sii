using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GrunflexPOS2.Diagnostics;

/// <summary>Orquestador de chequeos. Mantiene los <see cref="DiagnosticItem"/> bindables y los ejecuta en paralelo.</summary>
public sealed class DiagnosticsService
{
    private readonly List<IDiagnosticCheck> _checks;

    public DiagnosticsService(IEnumerable<IDiagnosticCheck>? checks = null)
    {
        _checks = checks?.ToList() ?? DefaultChecks().ToList();
        Items = new ObservableCollection<DiagnosticItem>(_checks.Select(c => new DiagnosticItem(c)));
    }

    public ObservableCollection<DiagnosticItem> Items { get; }

    public async Task RunAllAsync(CancellationToken ct, IProgress<int>? progress = null)
    {
        var total = Items.Count;
        var completed = 0;
        // Ejecución paralela con un límite suave para no saturar
        var sem = new SemaphoreSlim(4);
        var tasks = Items.Select(async item =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await item.RunAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
                Interlocked.Increment(ref completed);
                progress?.Report(completed * 100 / Math.Max(1, total));
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static IEnumerable<IDiagnosticCheck> DefaultChecks()
    {
        yield return new SqliteCheck();
        yield return new ApiServiceCheck();
        yield return new ApiHealthCheck();
        yield return new SmbCheck();
        yield return new LanCheck();
        yield return new FirewallCheck();
        yield return new PrinterCheck();
        yield return new WebView2Check();
        yield return new LicenseCheck();
        yield return new InternetCheck();
        yield return new BackupsCheck();
        yield return new OfflineQueueCheck();
        yield return new UpdatesCheck();
        yield return new TerminalRegistrationCheck();
        yield return new TelemetryCheck();
    }
}
