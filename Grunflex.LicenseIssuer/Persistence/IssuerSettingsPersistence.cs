using System.IO;
using System.Text.Json;
using Grunflex.LicenseIssuer.ViewModels;

namespace Grunflex.LicenseIssuer.Persistence;

public static class IssuerSettingsPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string GetFilePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Grunflex",
            "LicenseIssuer",
            "settings.json");

    public static IssuerSettingsDto? TryLoad()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<IssuerSettingsDto>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(IssuerSettingsDto dto)
    {
        var dir = Path.GetDirectoryName(GetFilePath());
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(GetFilePath(), JsonSerializer.Serialize(dto, JsonOptions));
    }

    /// <summary>Mapea desde el ViewModel hacia DTO para guardar.</summary>
    public static IssuerSettingsDto FromViewModel(MainViewModel vm) =>
        new()
        {
            ApiBaseUrl = string.IsNullOrWhiteSpace(vm.ApiBaseUrl) ? null : vm.ApiBaseUrl.Trim(),
            SmtpHost = vm.SmtpHost,
            SmtpPort = vm.SmtpPort,
            SenderEmail = vm.SenderEmail,
            ForcePasswordRotation = vm.ForcePasswordRotation,
            LockAfterFailedAttempts = vm.LockAfterFailedAttempts,
            EnableAuditLog = vm.EnableAuditLog,
            BackupFrequency = vm.BackupFrequency,
            BackupPath = vm.BackupPath,
            TimeZone = vm.TimeZone,
            Language = vm.Language,
            OnlineValidation = vm.OnlineValidation,
            SystemNotifications = vm.SystemNotifications,
            AutomaticBackups = vm.AutomaticBackups,
            MaintenanceMode = vm.MaintenanceMode,
            ActivationId = vm.ActivationId
        };

    public static void ApplyTo(MainViewModel vm, IssuerSettingsDto d)
    {
        if (!string.IsNullOrWhiteSpace(d.ApiBaseUrl))
            vm.ApiBaseUrl = d.ApiBaseUrl.Trim();
        vm.SmtpHost = d.SmtpHost;
        vm.SmtpPort = d.SmtpPort;
        vm.SenderEmail = d.SenderEmail;
        vm.ForcePasswordRotation = d.ForcePasswordRotation;
        vm.LockAfterFailedAttempts = d.LockAfterFailedAttempts;
        vm.EnableAuditLog = d.EnableAuditLog;
        vm.BackupFrequency = d.BackupFrequency;
        vm.BackupPath = d.BackupPath;
        vm.TimeZone = d.TimeZone;
        vm.Language = d.Language;
        vm.OnlineValidation = d.OnlineValidation;
        vm.SystemNotifications = d.SystemNotifications;
        vm.AutomaticBackups = d.AutomaticBackups;
        vm.MaintenanceMode = d.MaintenanceMode;
        if (!string.IsNullOrWhiteSpace(d.ActivationId))
            vm.ActivationId = d.ActivationId.Trim();
    }
}
