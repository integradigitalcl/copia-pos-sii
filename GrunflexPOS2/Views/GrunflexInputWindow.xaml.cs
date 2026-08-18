using System.Windows;
using System.Windows.Input;

namespace GrunflexPOS2.Views;

public partial class GrunflexInputWindow : Window
{
    public string Value { get; private set; } = string.Empty;

    public GrunflexInputWindow(string prompt, string title, string defaultValue)
    {
        InitializeComponent();
        TxtTitle.Text = title;
        TxtPrompt.Text = prompt;
        TxtInput.Text = defaultValue ?? string.Empty;
        Loaded += (_, _) =>
        {
            TxtInput.Focus();
            TxtInput.SelectAll();
        };
    }

    private void BtnAceptar_Click(object sender, RoutedEventArgs e) => Accept();

    private void BtnCancelar_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TxtInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
    }

    private void Accept()
    {
        Value = TxtInput.Text ?? string.Empty;
        DialogResult = true;
    }
}
