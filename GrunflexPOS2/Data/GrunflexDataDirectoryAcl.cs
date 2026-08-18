using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GrunflexPOS2.Data;

/// <summary>Permisos NTFS para que el usuario interactivo pueda escribir SQLite en ProgramData.</summary>
public static class GrunflexDataDirectoryAcl
{
    public static bool VerifyWriteAccess(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".grunflex-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Repara ACL + quita atributo ReadOnly en la BD. Requiere elevación para ProgramData.</summary>
    public static bool TryRepairCommerceDatabase()
    {
        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GrunflexPOS");

            foreach (var sub in new[] { "data", "config", "logs" })
            {
                var path = Path.Combine(baseDir, sub);
                Directory.CreateDirectory(path);
                TryGrantInteractiveUsersModify(path, fixExistingFiles: sub == "data");
            }

            TryIcaclsGrantTree(baseDir);

            var db = LocalDatabasePaths.DatabaseFilePath;
            if (File.Exists(db))
            {
                var attr = File.GetAttributes(db);
                if ((attr & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(db, attr & ~FileAttributes.ReadOnly);
            }

            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var aux = db + suffix;
                if (!File.Exists(aux)) continue;
                var a = File.GetAttributes(aux);
                if ((a & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(aux, a & ~FileAttributes.ReadOnly);
            }

            return VerifyWriteAccess(LocalDatabasePaths.DataDirectory);
        }
        catch
        {
            return false;
        }
    }

    private static void TryGrantInteractiveUsersModify(string directoryPath, bool fixExistingFiles)
    {
        var ruleUsers = RuleFor(WellKnownSidType.BuiltinUsersSid);
        var ruleAuth = RuleFor(WellKnownSidType.AuthenticatedUserSid);

        var dir = new DirectoryInfo(directoryPath);
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(ruleUsers);
        acl.AddAccessRule(ruleAuth);
        try
        {
            var current = WindowsIdentity.GetCurrent().User;
            if (current != null)
                acl.AddAccessRule(new FileSystemAccessRule(
                    current,
                    FileSystemRights.Modify | FileSystemRights.ReadAndExecute | FileSystemRights.Write,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
        }
        catch { /* noop */ }

        dir.SetAccessControl(acl);

        if (!fixExistingFiles)
            return;

        foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    file.Attributes &= ~FileAttributes.ReadOnly;
                var facl = file.GetAccessControl();
                facl.AddAccessRule(ruleUsers);
                facl.AddAccessRule(ruleAuth);
                file.SetAccessControl(facl);
            }
            catch { /* noop */ }
        }
    }

    private static FileSystemAccessRule RuleFor(WellKnownSidType sid) =>
        new(
            new SecurityIdentifier(sid, null),
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
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "icacls.exe",
                    Arguments = $"\"{path}\" /grant *S-1-5-32-545:(OI)(CI)M /grant *S-1-5-11:(OI)(CI)M /T /C",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                p?.WaitForExit(60_000);
            }
        }
        catch { /* requiere elevación en ProgramData */ }
    }
}
