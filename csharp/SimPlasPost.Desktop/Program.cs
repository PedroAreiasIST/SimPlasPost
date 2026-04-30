using Avalonia;

namespace SimPlasPost.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Diag.Log("Program.Main start");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diag.Log("UnhandledException: " + e.ExceptionObject);
        try
        {
            Diag.Log("BuildAvaloniaApp...");
            var app = BuildAvaloniaApp();
            Diag.Log("StartWithClassicDesktopLifetime...");
            app.StartWithClassicDesktopLifetime(args);
            Diag.Log("StartWithClassicDesktopLifetime returned");
        }
        catch (Exception ex)
        {
            Diag.Log("Program.Main threw: " + ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
