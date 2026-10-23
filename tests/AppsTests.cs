namespace PharosGaming.Tests;

public class AppsTests
{
    [Fact]
    public void NativeScript_OneTransactionQuoted()
    {
        string s = AppInstaller.NativeScript("pacman", new[] { "mangohud lib32-mangohud", "vlc", "vlc" });
        Assert.Equal("pacman -S --needed --noconfirm 'mangohud' 'lib32-mangohud' 'vlc'\n", s);
        Assert.Contains("apt-get install -y 'vlc'", AppInstaller.NativeScript("apt", new[] { "vlc" }));
    }

    [Fact]
    public async Task Detect_ThisMachine()
    {
        var apps = AppInstaller.Catalog();
        await AppInstaller.Detect(apps);
        Assert.All(apps, a => Assert.False(string.IsNullOrEmpty(a.Status)));
        if (AppInstaller.Manager() == "pacman" && Sys.Has("mangohud")) Assert.True(apps.First(a => a.Name == "MangoHud").Installed);
    }
}
