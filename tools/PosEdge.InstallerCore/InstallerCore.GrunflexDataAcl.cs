using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PosEdge.InstallerCore;

public static partial class InstallerCore
{
    /// <summary>
    /// La API (LocalSystem) y el POS (usuario interactivo) deben escribir la misma SQLite en ProgramData.
    /// Sin Modify para BUILTIN\Users falla "SQLite Error 8: readonly database" al crear el admin.
    /// </summary>
    public static void EnsureGrunflexMachineDataWritable()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GrunflexPOS");

        foreach (var sub in new[] { "data", "config", "logs" })
        {
            var path = Path.Combine(baseDir, sub);
            try
            {
                Directory.CreateDirectory(path);
                GrantInteractiveUsersModify(path, recursiveOnExistingFiles: sub == "data");
            }
            catch (Exception ex)
            {
                LogSetupFailure(new InvalidOperationException($"ACL GrunflexPOS/{sub}", ex));
            }
        }

        TryIcaclsGrantTree(baseDir);
        ClearReadOnlyOnCommerceSqliteFiles(Path.Combine(baseDir, "data"));
    }

    /// <summary>
    /// Tras arrancar GrunflexPOSAPI (crea <c>grunflex.db</c> como LocalSystem), reaplicar ACL en el árbol.
    /// </summary>
    public static void FinalizeGrunflexCommerceDataPermissions(int waitForDbMs = 15000)
    {
        var dataDir = GrunflexProgramDataDataDir;
        Directory.CreateDirectory(dataDir);

        var db = Path.Combine(dataDir, "grunflex.db");
        var deadline = Environment.TickCount64 + waitForDbMs;
        while (!File.Exists(db) && Environment.TickCount64 < deadline)
            Thread.Sleep(400);

        EnsureGrunflexMachineDataWritable();
    }

    private static void GrantInteractiveUsersModify(string directoryPath, bool recursiveOnExistingFiles)
    {
        if (!Directory.Exists(directoryPath))
            return;

        var ruleUsers = AccessRuleFor(WellKnownSidType.BuiltinUsersSid);
        var ruleAuth = AccessRuleFor(WellKnownSidType.AuthenticatedUserSid);

        var dirInfo = new DirectoryInfo(directoryPath);
        var dirAcl = dirInfo.GetAccessControl();
        dirAcl.AddAccessRule(ruleUsers);
        dirAcl.AddAccessRule(ruleAuth);
        try
        {
            var current = WindowsIdentity.GetCurrent().User;
            if (current != null)
                dirAcl.AddAccessRule(new FileSystemAccessRule(
                    current,
                    FileSystemRights.Modify | FileSystemRights.ReadAndExecute | FileSystemRights.Write,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
        }
        catch { /* noop */ }

        dirInfo.SetAccessControl(dirAcl);

        if (!recursiveOnExistingFiles)
            return;

        foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    file.Attributes &= ~FileAttributes.ReadOnly;

                var fileAcl = file.GetAccessControl();
                fileAcl.AddAccessRule(ruleUsers);
                fileAcl.AddAccessRule(ruleAuth);
                file.SetAccessControl(fileAcl);
            }
            catch { /* best-effort */ }
        }
    }

    private static FileSystemAccessRule AccessRuleFor(WellKnownSidType sidType) =>
        new(
            new SecurityIdentifier(sidType, null),
            FileSystemRights.Modify | FileSystemRights.ReadAndExecute | FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static void TryIcaclsGrantTree(string grunflexBase)
    {
        try
        {
            foreach (var sub in new[] { "data", "config" })
            {
                var path = Path.Combine(grunflexBase, sub);
                if (!Directory.Exists(path))
                    continue;
                // BUILTIN\Users + Authenticated Users, heredado en todo el árbol.
                RunIcacls($"\"{path}\" /grant *S-1-5-32-545:(OI)(CI)M /grant *S-1-5-11:(OI)(CI)M /T /C");
            }
        }
        catch (Exception ex)
        {
            LogSetupFailure(new InvalidOperationException("icacls GrunflexPOS", ex));
        }
    }

    private static void RunIcacls(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        p?.WaitForExit(60_000);
    }

    private static void ClearReadOnlyOnCommerceSqliteFiles(string dataDir)
    {
        if (!Directory.Exists(dataDir))
            return;
        foreach (var pattern in new[] { "grunflex.db", "grunflex.db-wal", "grunflex.db-shm", "grunflex_api.db*" })
        {
            try
            {
                foreach (var f in Directory.GetFiles(dataDir, pattern))
                {
                    var attr = File.GetAttributes(f);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                }
            }
            catch { /* noop */ }
        }
    }
}
