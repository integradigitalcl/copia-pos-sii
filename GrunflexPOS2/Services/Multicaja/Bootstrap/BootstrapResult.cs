namespace GrunflexPOS2.Services.Multicaja.Bootstrap;

public enum BootstrapStatus
{
    Ready,
    Failed,
    NotApplicable
}

public sealed class BootstrapResult
{
    public BootstrapStatus Status { get; init; }
    public string UserMessage { get; init; } = "";

    public static BootstrapResult Ready() =>
        new() { Status = BootstrapStatus.Ready };

    public static BootstrapResult NotApplicable() =>
        new() { Status = BootstrapStatus.NotApplicable };

    public static BootstrapResult Fail(string message) =>
        new() { Status = BootstrapStatus.Failed, UserMessage = message };
}
