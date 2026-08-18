using System.Windows;

namespace Grunflex.LicenseIssuer.Dialogs;

public partial class EditClientDialog : Window
{
    public string NameResult => TxtName.Text.Trim();

    public string BusinessResult => TxtBusiness.Text.Trim();

    public string EmailResult => TxtEmail.Text.Trim();

    public string PhoneResult => TxtPhone.Text.Trim();

    public EditClientDialog(string title, string name, string business, string email, string phone)
    {
        InitializeComponent();
        Title = title;
        TxtName.Text = name;
        TxtBusiness.Text = business;
        TxtEmail.Text = email;
        TxtPhone.Text = phone;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text) || string.IsNullOrWhiteSpace(TxtBusiness.Text))
        {
            MessageBox.Show("Nombre y negocio son obligatorios.", "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
