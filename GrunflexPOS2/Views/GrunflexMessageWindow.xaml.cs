using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GrunflexPOS2.Views;

public partial class GrunflexMessageWindow : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public GrunflexMessageWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        TxtTitle.Text = title;
        TxtMessage.Text = message;
        ApplyIcon(icon);
        BuildButtons(buttons);
    }

    private void ApplyIcon(MessageBoxImage icon)
    {
        switch (icon)
        {
            case MessageBoxImage.Warning:
                IconCircle.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFB, 0xEB));
                IconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
                IconGlyph.Text = "\uE7BA";
                break;
            case MessageBoxImage.Error:
                IconCircle.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2));
                IconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                IconGlyph.Text = "\uE783";
                break;
            case MessageBoxImage.Question:
                IconCircle.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF));
                IconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
                IconGlyph.Text = "\uE897";
                break;
            default:
                IconCircle.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF));
                IconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
                IconGlyph.Text = "\uE946";
                break;
        }
    }

    private void BuildButtons(MessageBoxButton buttons)
    {
        PanelButtons.Children.Clear();

        if (buttons == MessageBoxButton.YesNo || buttons == MessageBoxButton.YesNoCancel)
        {
            PanelButtons.Children.Add(CreateButton("No", false, MessageBoxResult.No));
            PanelButtons.Children.Add(CreateButton("Sí", true, MessageBoxResult.Yes));
            return;
        }

        if (buttons == MessageBoxButton.OKCancel)
        {
            PanelButtons.Children.Add(CreateButton("Cancelar", false, MessageBoxResult.Cancel));
            PanelButtons.Children.Add(CreateButton("Aceptar", true, MessageBoxResult.OK));
            return;
        }

        PanelButtons.Children.Add(CreateButton("Aceptar", true, MessageBoxResult.OK));
    }

    private Button CreateButton(string text, bool primary, MessageBoxResult result)
    {
        var btn = new Button
        {
            Content = text,
            MinWidth = primary ? 120 : 100,
            Height = 40,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = primary,
            IsCancel = !primary && result is MessageBoxResult.Cancel or MessageBoxResult.No,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        };

        if (primary)
        {
            btn.Foreground = Brushes.White;
            btn.Template = CreatePrimaryButtonTemplate();
        }
        else
        {
            btn.Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            btn.Template = CreateSecondaryButtonTemplate();
        }

        btn.Click += (_, _) =>
        {
            Result = result;
            DialogResult = true;
        };

        return btn;
    }

    private static ControlTemplate CreatePrimaryButtonTemplate()
    {
        const string xaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="Button">
                <Border x:Name="bd" Background="#1D4ED8" CornerRadius="8" Padding="16,0">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="bd" Property="Background" Value="#1E40AF"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="bd" Property="Opacity" Value="0.92"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
            """;
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static ControlTemplate CreateSecondaryButtonTemplate()
    {
        const string xaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="Button">
                <Border x:Name="bd"
                        Background="White"
                        BorderBrush="#D1D5DB"
                        BorderThickness="1"
                        CornerRadius="8"
                        Padding="16,0">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="bd" Property="Background" Value="#F9FAFB"/>
                        <Setter TargetName="bd" Property="BorderBrush" Value="#9CA3AF"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
            """;
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }
}
