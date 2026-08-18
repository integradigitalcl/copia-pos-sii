using System.IO;
using System.Linq;
using System.Windows;

namespace PosEdge.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args ?? Array.Empty<string>();
        var payloadRoot = ResolvePayloadRoot(args);
        var repair = args.Any(a => string.Equals(a, "--repair", StringComparison.OrdinalIgnoreCase));
        var postInstall = args.Any(a => string.Equals(a, "--post-install", StringComparison.OrdinalIgnoreCase));
        var module = ParseModule(args);

        var splash = new SplashWindow();
        MainWindow = splash;
        splash.Show();
        splash.Activate();

        // Never block the UI thread: splash must stay responsive.
        _ = RunLauncherAsync(splash, payloadRoot, repair, postInstall, module);
    }

    private async Task RunLauncherAsync(SplashWindow splash, string payloadRoot, bool repair, bool postInstall, string? module)
    {
        try
        {
            var host = new LauncherHost(payloadRoot, repair, postInstall, splash, module);
            var code = await Task.Run(async () => await host.RunAsync().ConfigureAwait(false)).ConfigureAwait(true);
            splash.Close();
            Shutdown(code);
        }
        catch (Exception ex)
        {
            PosEdge.InstallerCore.InstallerCore.LogSetupFailure(ex);
            splash.Close();
            Shutdown(20);
        }
    }

    private static string ResolvePayloadRoot(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--payload-root", StringComparison.OrdinalIgnoreCase))
                return args[i + 1].Trim('"');
        }

        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(baseDir)?.FullName;
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(Path.Combine(parent, "GrunflexPOS")))
            return parent;

        return baseDir;
    }

    private static string? ParseModule(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--module", StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i + 1]?.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}
