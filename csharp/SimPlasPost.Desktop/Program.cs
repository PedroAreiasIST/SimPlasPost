using Avalonia;

namespace SimPlasPost.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Ab4d.SharpEngine license (free trial: https://www.ab4d.com/trial)
        // Uncomment and paste your trial key to suppress the evaluation dialog:
        // Ab4d.SharpEngine.Licensing.SetLicense(
        //     licenseOwner: "your-name",
        //     licenseType:  "trial",
        //     license:      "paste-your-trial-key-here");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
