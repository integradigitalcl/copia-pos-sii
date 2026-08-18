using System.Windows;
using GrunflexPOS2.Views;

namespace GrunflexPOS2.UI;

/// <summary>Diálogos con estilo Aero del POS (reemplazo visual de MessageBox / InputBox clásicos).</summary>
public static class GrunflexDialogs
{
    public static MessageBoxResult Show(
        string message,
        string caption = "",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        Window? owner = null)
    {
        var w = new GrunflexMessageWindow(
            message,
            string.IsNullOrWhiteSpace(caption) ? "Grunflex POS" : caption,
            button,
            icon)
        {
            Owner = ResolveOwner(owner)
        };
        w.ShowDialog();
        return w.Result;
    }

    /// <summary>Compatible con <c>Interaction.InputBox</c>: cadena vacía si cancela.</summary>
    public static string Input(string prompt, string title, string defaultValue = "", Window? owner = null)
    {
        var w = new GrunflexInputWindow(prompt, title, defaultValue)
        {
            Owner = ResolveOwner(owner)
        };
        return w.ShowDialog() == true ? w.Value : string.Empty;
    }

    private static Window? ResolveOwner(Window? owner)
    {
        if (owner != null)
            return owner;

        return System.Windows.Application.Current?.MainWindow is { IsLoaded: true } mw ? mw : null;
    }
}
