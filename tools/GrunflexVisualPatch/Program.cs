using System.Diagnostics;
using System.Text;

namespace GrunflexVisualPatch;

internal static class Program
{
    private const string ProcessName = "GrunflexPOS2";
    private const string ExeName = "GrunflexPOS2.exe";

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            return RunPatch();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error al aplicar el parche:\n\n{ex.Message}",
                "Grunflex — parche visual",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int RunPatch()
    {
        string patchRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string payloadDir = Path.Combine(patchRoot, "payload");

        if (!Directory.Exists(payloadDir))
        {
            MessageBox.Show(
                $"No se encontró la carpeta \"payload\" junto al parche.\n\nRuta esperada:\n{payloadDir}",
                "Grunflex — parche visual",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        if (!File.Exists(Path.Combine(payloadDir, ExeName)))
        {
            MessageBox.Show(
                $"El payload no contiene {ExeName}.",
                "Grunflex — parche visual",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        string? installDir = ResolverInstalacion();
        if (installDir == null)
            return 0;

        var confirm = MessageBox.Show(
            $"Se aplicará el parche visual en:\n\n{installDir}\n\n" +
            "• Se cerrará Grunflex POS si está abierto.\n" +
            "• Se hará copia de seguridad de los archivos reemplazados.\n\n" +
            "¿Continuar?",
            "Grunflex — parche visual",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes)
            return 0;

        DetenerPosSiCorre();

        string backupDir = Path.Combine(
            Path.GetTempPath(),
            $"GrunflexVisualPatch-backup-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backupDir);

        int archivos = CopiarPayload(payloadDir, installDir, backupDir);

        MessageBox.Show(
            $"Parche aplicado correctamente.\n\n" +
            $"Archivos actualizados: {archivos}\n" +
            $"Instalación: {installDir}\n" +
            $"Respaldo: {backupDir}\n\n" +
            "Abra Grunflex POS para ver los cambios visuales.",
            "Grunflex — parche visual",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        return 0;
    }

    private static string? ResolverInstalacion()
    {
        var candidatos = ObtenerCandidatosInstalacion().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (candidatos.Count == 1)
            return candidatos[0];

        if (candidatos.Count > 1)
        {
            using var dlg = new Form
            {
                Text = "Grunflex — parche visual",
                Width = 520,
                Height = 220,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen
            };

            var lbl = new Label
            {
                Text = "Seleccione la carpeta donde está instalado Grunflex POS:",
                Left = 16,
                Top = 16,
                Width = 470,
                Height = 36
            };

            var combo = new ComboBox
            {
                Left = 16,
                Top = 56,
                Width = 470,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            combo.Items.AddRange(candidatos.Cast<object>().ToArray());
            combo.SelectedIndex = 0;

            var btnOk = new Button { Text = "Aplicar", Left = 300, Top = 120, Width = 90, DialogResult = DialogResult.OK };
            var btnBrowse = new Button { Text = "Examinar…", Left = 200, Top = 120, Width = 90 };
            var btnCancel = new Button { Text = "Cancelar", Left = 396, Top = 120, Width = 90, DialogResult = DialogResult.Cancel };

            btnBrowse.Click += (_, _) =>
            {
                using var folder = new FolderBrowserDialog
                {
                    Description = "Carpeta de instalación de Grunflex POS (contiene GrunflexPOS2.exe)",
                    UseDescriptionForTitle = true
                };
                if (folder.ShowDialog() != DialogResult.OK)
                    return;

                if (File.Exists(Path.Combine(folder.SelectedPath, ExeName)))
                {
                    combo.Items.Insert(0, folder.SelectedPath);
                    combo.SelectedIndex = 0;
                }
                else
                {
                    MessageBox.Show(dlg, "Esa carpeta no contiene GrunflexPOS2.exe.", "Grunflex",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            dlg.Controls.AddRange(new Control[] { lbl, combo, btnOk, btnBrowse, btnCancel });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            return dlg.ShowDialog() == DialogResult.OK ? combo.SelectedItem?.ToString() : null;
        }

        using var browse = new FolderBrowserDialog
        {
            Description = "No se detectó Grunflex POS. Indique la carpeta que contiene GrunflexPOS2.exe",
            UseDescriptionForTitle = true
        };

        if (browse.ShowDialog() != DialogResult.OK)
            return null;

        if (!File.Exists(Path.Combine(browse.SelectedPath, ExeName)))
        {
            MessageBox.Show(
                "La carpeta seleccionada no contiene GrunflexPOS2.exe.",
                "Grunflex — parche visual",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        return browse.SelectedPath;
    }

    private static IEnumerable<string> ObtenerCandidatosInstalacion()
    {
        foreach (var proc in Process.GetProcessesByName(ProcessName))
        {
            string? dir = TryGetProcessDirectory(proc);
            if (dir != null)
                yield return dir;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");

        string[] relativePaths =
        {
            Path.Combine("GrunflexPOS"),
            Path.Combine("PosEdge", "GrunflexPOS"),
            Path.Combine("Grunflex", "GrunflexPOS"),
        };

        foreach (string root in new[] { programFiles, programFilesX86, localPrograms, Path.GetTempPath() })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            foreach (string rel in relativePaths)
            {
                string dir = Path.Combine(root, rel);
                if (File.Exists(Path.Combine(dir, ExeName)))
                    yield return dir;
            }
        }

        string hotfix = Path.Combine(Path.GetTempPath(), "grunflex-pos-hotfix");
        if (File.Exists(Path.Combine(hotfix, ExeName)))
            yield return hotfix;
    }

    private static string? TryGetProcessDirectory(Process proc)
    {
        try
        {
            string? path = proc.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
                return null;
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    private static void DetenerPosSiCorre()
    {
        foreach (var proc in Process.GetProcessesByName(ProcessName))
        {
            try
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                    proc.CloseMainWindow();

                if (!proc.WaitForExit(8000))
                    proc.Kill(entireProcessTree: true);

                proc.WaitForExit(3000);
            }
            catch
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static int CopiarPayload(string payloadDir, string installDir, string backupDir)
    {
        int count = 0;
        var log = new StringBuilder();
        log.AppendLine($"Parche visual Grunflex POS — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"Origen: {payloadDir}");
        log.AppendLine($"Destino: {installDir}");
        log.AppendLine();

        foreach (string sourceFile in Directory.EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(payloadDir, sourceFile);
            string destFile = Path.Combine(installDir, relative);
            string? destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            if (File.Exists(destFile))
            {
                string backupFile = Path.Combine(backupDir, relative);
                string? backupParent = Path.GetDirectoryName(backupFile);
                if (!string.IsNullOrEmpty(backupParent))
                    Directory.CreateDirectory(backupParent);
                File.Copy(destFile, backupFile, overwrite: true);
            }

            File.Copy(sourceFile, destFile, overwrite: true);
            log.AppendLine(relative);
            count++;
        }

        File.WriteAllText(Path.Combine(backupDir, "patch-log.txt"), log.ToString(), Encoding.UTF8);
        return count;
    }
}
