using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GrunflexPOS2.Diagnostics;

namespace GrunflexPOS2.Views;

public partial class DiagnosticoView : Window
{
    private readonly DiagnosticsService _svc = new();
    private CancellationTokenSource? _cts;

    public DiagnosticoView()
    {
        InitializeComponent();
        Lista.ItemsSource = _svc.Items;
        Loaded += async (_, _) => await EjecutarTodosAsync();
    }

    private async Task EjecutarTodosAsync()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        try
        {
            BtnRecheck.IsEnabled = false;
            BtnRepararTodo.IsEnabled = false;
            ProgresoGlobal.Value = 0;
            EstadoGlobal.Text = "Comprobando...";
            var progress = new Progress<int>(p => ProgresoGlobal.Value = p);
            await _svc.RunAllAsync(_cts.Token, progress);
            ResumirEstadoGlobal();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show("Error al ejecutar diagnóstico:\n\n" + ex.Message, "Diagnóstico",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnRecheck.IsEnabled = true;
            BtnRepararTodo.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ResumirEstadoGlobal()
    {
        var errores = _svc.Items.Count(x => x.Status == DiagnosticStatus.Error);
        var warns   = _svc.Items.Count(x => x.Status == DiagnosticStatus.Warning);
        var ok      = _svc.Items.Count(x => x.Status == DiagnosticStatus.Ok);

        if (errores > 0)
            EstadoGlobal.Text = $"{errores} problema(s) crítico(s) — {warns} advertencia(s) — {ok} ok.";
        else if (warns > 0)
            EstadoGlobal.Text = $"Sin errores críticos. {warns} advertencia(s) — {ok} ok.";
        else
            EstadoGlobal.Text = $"Todo en orden ({ok} chequeos ok).";
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        await EjecutarTodosAsync();
    }

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not DiagnosticItem item) return;
        if (item.FixAction == null) return;

        item.IsFixing = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var msg = await item.FixAction(cts.Token).ConfigureAwait(true);
            MessageBox.Show(msg, "Auto-reparación: " + item.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            await item.RunAsync(CancellationToken.None).ConfigureAwait(true);
            ResumirEstadoGlobal();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Falló la reparación:\n\n" + ex.Message, item.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            item.IsFixing = false;
        }
    }

    private async void RepararTodo_Click(object sender, RoutedEventArgs e)
    {
        var reparables = _svc.Items.Where(x => x.CanAutoFix && x.FixAction != null).ToList();
        if (reparables.Count == 0)
        {
            MessageBox.Show("No hay problemas con reparación automática disponible.", "Reparar todo",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(
            $"Se intentarán reparar {reparables.Count} problema(s) detectados. ¿Continuar?",
            "Reparar todo", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var resultados = new StringBuilder();
        foreach (var item in reparables)
        {
            item.IsFixing = true;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var msg = await item.FixAction!(cts.Token).ConfigureAwait(true);
                resultados.AppendLine($"- {item.DisplayName}: {msg}");
                await item.RunAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                resultados.AppendLine($"- {item.DisplayName}: ERROR {ex.Message}");
            }
            finally
            {
                item.IsFixing = false;
            }
        }
        ResumirEstadoGlobal();
        MessageBox.Show(resultados.ToString(), "Resultado de reparaciones",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Copiar_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== Grunflex POS — Reporte de diagnóstico =====");
        sb.AppendLine("Fecha: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("Equipo: " + Environment.MachineName);
        sb.AppendLine("Usuario: " + Environment.UserName);
        sb.AppendLine("OS: " + Environment.OSVersion);
        sb.AppendLine();
        foreach (var item in _svc.Items)
        {
            sb.AppendLine($"[{item.Status}] {item.DisplayName} ({item.Category})");
            sb.AppendLine("  " + item.Summary);
            if (!string.IsNullOrWhiteSpace(item.TechnicalDetails))
            {
                foreach (var line in item.TechnicalDetails.Split('\n'))
                    sb.AppendLine("    " + line.TrimEnd('\r'));
            }
            sb.AppendLine();
        }
        try
        {
            Clipboard.SetText(sb.ToString());
            // Guardar también a logs para soporte
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS", "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(logPath, sb.ToString());
            MessageBox.Show("Reporte copiado al portapapeles y guardado en:\n" + logPath,
                "Reporte de diagnóstico", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo guardar/copiar:\n" + ex.Message, "Reporte de diagnóstico",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
