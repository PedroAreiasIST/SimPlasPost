using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SimPlasPost.Desktop.Views;

namespace SimPlasPost.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        Diag.Log("App.Initialize");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Diag.Log("App.OnFrameworkInitializationCompleted");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Diag.Log("new MainWindow...");
            desktop.MainWindow = new MainWindow();
            Diag.Log("MainWindow constructed");
        }
        base.OnFrameworkInitializationCompleted();
        Diag.Log("App.OnFrameworkInitializationCompleted done");
    }
}
