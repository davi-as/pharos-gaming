using Avalonia;
using Avalonia.Media;

namespace PharosGaming;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect()
            // fonte do Pharos (Fira Sans) em tudo, inclusive menus e dicas
            .With(new FontManagerOptions { DefaultFamilyName = "avares://pharos-gaming/Assets/Fonts#Fira Sans" })
            .StartWithClassicDesktopLifetime(args);
}
