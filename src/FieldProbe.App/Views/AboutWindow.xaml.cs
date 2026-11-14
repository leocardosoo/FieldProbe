using System.IO;
using System.Windows;
using FieldProbe.App.Services;

namespace FieldProbe.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Versão {AppInfo.Version}";
        CopyrightText.Text = AppInfo.Copyright;

        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        NoticesText.Text = File.Exists(notices)
            ? File.ReadAllText(notices)
            : "Arquivo THIRD-PARTY-NOTICES.md não encontrado na pasta do programa.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
