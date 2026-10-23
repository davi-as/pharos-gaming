namespace PharosGaming.Tests;

public class StartupTests
{
    [Fact]
    public void Desktop_UserOverrideHidesSystemEntryAndRespectsShowIn()
    {
        string root = Directory.CreateTempSubdirectory("autostart").FullName;
        string sys = Path.Combine(root, "sys"), usr = Path.Combine(root, "usr");
        Directory.CreateDirectory(sys); Directory.CreateDirectory(usr);
        File.WriteAllText(Path.Combine(sys, "nm-applet.desktop"), "[Desktop Entry]\nName=Rede\nExec=sh -c true\n");
        File.WriteAllText(Path.Combine(sys, "kde-only.desktop"), "[Desktop Entry]\nName=KDE\nExec=sh\nOnlyShowIn=KDE;\n");
        File.WriteAllText(Path.Combine(usr, "nm-applet.desktop"), StartupManager.SetHidden("[Desktop Entry]\nName=Rede\nExec=sh -c true\n", true));
        File.WriteAllText(Path.Combine(usr, "meu.desktop"), "[Desktop Entry]\nName=Meu script\nExec=/nao/existe --x\n");
        var list = StartupManager.ListDesktop(sys, usr, "Hyprland");
        Assert.Equal(2, list.Count);                                       // o do KDE não roda no Hyprland
        var nm = list.Single(e => e.Name == "Rede");
        Assert.False(nm.Enabled);
        Assert.False(nm.CanRemove);                                        // do sistema: só desliga
        var mine = list.Single(e => e.Name == "Meu script");
        Assert.True(mine.FileMissing);
        Assert.True(mine.CanRemove);
    }

    [Fact]
    public void SetHidden_KeepsOtherKeys()
    {
        string t = StartupManager.SetHidden("[Desktop Entry]\nName=X\nHidden=true\nX-GNOME-Autostart-enabled=false\nExec=x\n", false);
        Assert.Contains("Hidden=false", t);
        Assert.DoesNotContain("Hidden=true", t);
        Assert.DoesNotContain("X-GNOME", t);
        Assert.Contains("Exec=x", t);
    }
}
