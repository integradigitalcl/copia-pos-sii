using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Networking
{
    /// <summary>
    /// Antes de abrir SQLite sobre UNC: credenciales en almacén de Windows (cmdkey),
    /// WNet y <c>net use</c> con la sintaxis correcta (contraseña después del UNC).
    /// </summary>
    internal static class SmbShareConnectionBootstrap
    {
        private const int ResourceTypeDisk = 0x00000001;
        private const int NoError = 0;
        private const int ErrorAlreadyAssigned = 85;
        private const int ErrorDeviceAlreadyRemembered = 1202;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NetResourceW
        {
            public int Scope;
            public int ResourceType;
            public int DisplayType;
            public int Usage;
            public string LocalName;
            public string RemoteName;
            public string Comment;
            public string Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int WNetAddConnection2(ref NetResourceW netResource, string? password, string? userName, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);

        /// <summary>
        /// Devuelve true si el recurso UNC responde (carpeta o archivo de BD).
        /// </summary>
        public static bool TryEnsureShareForSqlite(AppConfig cfg)
        {
            try
            {
                var cs = cfg.ConnectionString ?? string.Empty;
                if (!cs.Contains(@"\\", StringComparison.Ordinal))
                    return true;

                var dataSource = SqliteConnectionStringHelpers.ExtractDataSource(cs);
                if (string.IsNullOrWhiteSpace(dataSource) || !dataSource.StartsWith(@"\\", StringComparison.Ordinal))
                    return true;

                var shareRoot = Path.GetDirectoryName(dataSource);
                if (string.IsNullOrWhiteSpace(shareRoot) || !shareRoot.StartsWith(@"\\", StringComparison.Ordinal))
                    return true;

                var user = string.IsNullOrWhiteSpace(cfg.SmbShareUser)
                    ? MulticajaLanDefaults.ShareUser
                    : cfg.SmbShareUser.Trim();

                var pass = string.IsNullOrWhiteSpace(cfg.SmbSharePassword)
                    ? MulticajaLanDefaults.SharePassword
                    : cfg.SmbSharePassword;

                var host = ExtraerHostDesdeUncShare(shareRoot);
                if (string.IsNullOrEmpty(host))
                    return false;

                LogTcp445Reachable(host);

                // Credenciales en el almacén de Windows (mismo destino que usa el redirector SMB).
                TryCmdKeyCredential(host, $"{host}\\{user}", pass);

                var ipcPath = $@"\\{host}\IPC$";
                var userVariants = new[]
                {
                    $"{host}\\{user}",
                    $".\\{user}",
                    user
                };

                // 1) Sin cortar sesiones previas: suele funcionar si ya hay mapping válido.
                if (TryConnectSequence(shareRoot, ipcPath, pass, userVariants))
                    return WaitShareVisible(shareRoot, dataSource);

                // 2) Segundo intento: limpiar mapping del share y repetir (IPC$ + share + WNet).
                _ = WNetCancelConnection2(shareRoot, 0, true);
                _ = WNetCancelConnection2(ipcPath, 0, true);
                if (TryConnectSequence(shareRoot, ipcPath, pass, userVariants))
                    return WaitShareVisible(shareRoot, dataSource);

                PosDiagnostics.Log($"SMB: agotados intentos hacia {shareRoot} (cmdkey + IPC$ + WNet + net use).");
                return WaitShareVisible(shareRoot, dataSource);
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("SMB: excepción preparando recurso de red", ex);
                return false;
            }
        }

        private static void LogTcp445Reachable(string host)
        {
            try
            {
                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync(host, 445);
                if (!connectTask.Wait(4000))
                {
                    PosDiagnostics.Log($"SMB: TCP 445 a {host} timeout (firewall, PC apagado o red distinta).");
                    return;
                }

                PosDiagnostics.Log($"SMB: TCP 445 a {host} alcanzable.");
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log($"SMB: TCP 445 a {host} no disponible: {ex.Message}");
            }
        }

        /// <summary>
        /// Conexión por IP: primero <c>\\host\IPC$</c> con <c>net use</c>, luego el share; respaldo WNet al share.
        /// </summary>
        private static bool TryConnectSequence(string shareRoot, string ipcPath, string pass, string[] userVariants)
        {
            foreach (var domainUser in userVariants)
            {
                _ = RunNetUse(ipcPath, pass, domainUser, logLabel: "IPC$");
                if (RunNetUse(shareRoot, pass, domainUser, logLabel: "share") == 0)
                    return true;

                var nr = new NetResourceW
                {
                    Scope = 0,
                    ResourceType = ResourceTypeDisk,
                    DisplayType = 0,
                    Usage = 0,
                    LocalName = string.Empty,
                    RemoteName = shareRoot,
                    Comment = string.Empty,
                    Provider = string.Empty
                };

                var rc = WNetAddConnection2(ref nr, pass, domainUser, 0);
                if (rc is NoError or ErrorAlreadyAssigned or ErrorDeviceAlreadyRemembered)
                {
                    PosDiagnostics.Log($"SMB: WNet OK hacia {shareRoot} como {domainUser}.");
                    return true;
                }

                PosDiagnostics.Log(
                    $"SMB: WNetAddConnection2 rc={rc} win32={Marshal.GetLastWin32Error()} " +
                    $"hacia {shareRoot} usuario={domainUser}.");
            }

            return false;
        }

        /// <returns>Código de salida de net.exe (0 = éxito).</returns>
        private static int RunNetUse(string remote, string password, string userArg, string logLabel)
        {
            try
            {
                var netExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "net.exe");
                if (!File.Exists(netExe))
                    return -1;

                var psi = new ProcessStartInfo
                {
                    FileName = netExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("use");
                psi.ArgumentList.Add(remote);
                psi.ArgumentList.Add(password);
                psi.ArgumentList.Add($"/user:{userArg}");
                psi.ArgumentList.Add("/persistent:no");

                using var p = Process.Start(psi);
                if (p == null)
                    return -1;
                var err = p.StandardError.ReadToEnd();
                var outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(25_000);
                if (p.ExitCode != 0)
                    PosDiagnostics.Log($"SMB: net use ({logLabel}) exit={p.ExitCode} remote={remote} out={outp.Trim()} err={err.Trim()}".Trim());

                return p.ExitCode;
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log($"SMB: net use ({logLabel}) excepción hacia {remote}", ex);
                return -1;
            }
        }

        private static void TryCmdKeyCredential(string host, string userPrincipal, string password)
        {
            try
            {
                var cmdkey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmdkey.exe");
                if (!File.Exists(cmdkey))
                    return;

                RunCmdKey(cmdkey, new[] { "/delete:" + host });
                var (exit, _, err) = RunCmdKeyCapture(cmdkey, new[] { "/add:" + host, "/user:" + userPrincipal, "/pass:" + password });
                if (exit == 0)
                    PosDiagnostics.Log($"SMB: cmdkey /add:{host} OK.");
                else
                    PosDiagnostics.Log($"SMB: cmdkey /add falló exit={exit} err={err}".Trim());
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("SMB: cmdkey excepción", ex);
            }
        }

        private static void RunCmdKey(string cmdkey, string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmdkey,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var a in args)
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                p?.WaitForExit(8000);
            }
            catch { /* delete puede no existir */ }
        }

        private static (int exit, string stdout, string stderr) RunCmdKeyCapture(string cmdkey, string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmdkey,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null)
                return (-1, string.Empty, string.Empty);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(20_000);
            return (p.ExitCode, stdout, stderr);
        }

        private static bool WaitShareVisible(string shareRoot, string databaseFile)
        {
            for (var i = 0; i < 12; i++)
            {
                try
                {
                    if (File.Exists(databaseFile))
                        return true;
                    if (Directory.Exists(shareRoot))
                        return true;
                }
                catch
                {
                    // Red / credenciales: reintentar
                }

                System.Threading.Thread.Sleep(300);
            }

            try
            {
                return File.Exists(databaseFile) || Directory.Exists(shareRoot);
            }
            catch
            {
                return false;
            }
        }

        private static string ExtraerHostDesdeUncShare(string uncShareRoot)
        {
            var s = uncShareRoot.TrimStart('\\');
            var i = s.IndexOf('\\');
            return i > 0 ? s[..i] : string.Empty;
        }
    }
}
