using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace GrunflexPOS2.Diagnostics;

/// <summary>Item bindable que combina un check con su último resultado y estado de UI.</summary>
public sealed class DiagnosticItem : INotifyPropertyChanged
{
    private readonly IDiagnosticCheck _check;
    private DiagnosticStatus _status = DiagnosticStatus.Pending;
    private string _summary = string.Empty;
    private string _technical = string.Empty;
    private bool _canAutoFix;
    private bool _isExpanded;
    private bool _isFixing;

    public DiagnosticItem(IDiagnosticCheck check)
    {
        _check = check;
    }

    public string Id => _check.Id;
    public string DisplayName => _check.DisplayName;
    public string Category => _check.Category;

    public DiagnosticStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusIcon)); OnPropertyChanged(nameof(StatusColor)); }
    }

    public string Summary
    {
        get => _summary;
        set { _summary = value; OnPropertyChanged(); }
    }

    public string TechnicalDetails
    {
        get => _technical;
        set { _technical = value; OnPropertyChanged(); }
    }

    public bool CanAutoFix
    {
        get => _canAutoFix;
        set { _canAutoFix = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool IsFixing
    {
        get => _isFixing;
        set { _isFixing = value; OnPropertyChanged(); }
    }

    public Func<CancellationToken, Task<string>>? FixAction { get; set; }

    // Iconos Segoe MDL2 Assets
    public string StatusIcon => Status switch
    {
        DiagnosticStatus.Ok      => "\uE73E",   // check
        DiagnosticStatus.Warning => "\uE7BA",   // warning
        DiagnosticStatus.Error   => "\uEA39",   // error
        DiagnosticStatus.Running => "\uE895",   // sync
        DiagnosticStatus.Skipped => "\uE71C",   // info
        _                        => "\uE9CE"    // unknown / pending
    };

    public string StatusColor => Status switch
    {
        DiagnosticStatus.Ok      => "#16A34A",
        DiagnosticStatus.Warning => "#D97706",
        DiagnosticStatus.Error   => "#DC2626",
        DiagnosticStatus.Running => "#2563EB",
        DiagnosticStatus.Skipped => "#6B7280",
        _                        => "#9CA3AF"
    };

    public async Task RunAsync(CancellationToken ct)
    {
        Status = DiagnosticStatus.Running;
        Summary = "Comprobando...";
        TechnicalDetails = string.Empty;
        CanAutoFix = false;
        FixAction = null;
        try
        {
            var result = await _check.RunAsync(ct).ConfigureAwait(false);
            Status = result.Status;
            Summary = result.Summary;
            TechnicalDetails = result.TechnicalDetails ?? string.Empty;
            CanAutoFix = result.CanAutoFix && result.AutoFix is not null;
            FixAction = result.AutoFix;
        }
        catch (Exception ex)
        {
            Status = DiagnosticStatus.Error;
            Summary = "El chequeo falló inesperadamente.";
            TechnicalDetails = ex.ToString();
            CanAutoFix = false;
            FixAction = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
