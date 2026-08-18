using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GrunflexPOS2.Services.Multicaja.Bootstrap;

namespace GrunflexPOS2.Views;

public partial class TerminalBootstrapWindow : Window
{
    private readonly TerminalBootstrapOrchestrator _orchestrator;
    private readonly CancellationTokenSource _cts = new();

    public bool Success { get; private set; }

    public string FailureMessage { get; private set; } = "";

    public TerminalBootstrapWindow(TerminalBootstrapOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _orchestrator.RunAsync(SetStatus, _cts.Token).ConfigureAwait(true);
            Success = result.Status == BootstrapStatus.Ready;
            if (!Success)
                FailureMessage = result.UserMessage;
            else
                SetStatus("Abriendo inicio de sesión…");
        }
        catch (Exception ex)
        {
            Success = false;
            FailureMessage = ex.Message;
        }
        finally
        {
            // No usar DialogResult aquí: con Loaded async en WPF puede cerrar el diálogo
            // antes de que App.xaml.cs muestre LoginWindow (OnLastWindowClose).
            Close();
        }
    }

    private void SetStatus(string message)
    {
        Dispatcher.Invoke(() => TxtStatus.Text = message);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _cts.Cancel(); } catch { }
        base.OnClosed(e);
    }
}
