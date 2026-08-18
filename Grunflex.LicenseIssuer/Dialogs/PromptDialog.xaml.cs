using System.Windows;

namespace Grunflex.LicenseIssuer.Dialogs;

public partial class PromptDialog : Window
{
    public string? Result { get; private set; }

    public PromptDialog(string title, string label, string initialValue)
    {
        InitializeComponent();
        Title = title;
        Lbl.Text = label;
        Txt.Text = initialValue;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = Txt.Text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
