namespace PharosGaming.Tests;

public class GamesTests
{
    [Fact]
    public void Vdf_RoundTripKeepsNestingAndEscapes()
    {
        var root = Vdf.Parse("\"A\"\n{\n\t\"k\"\t\t\"say \\\"hi\\\"\"\n\t// comentário\n\t\"B\" { \"x\" \"1\" }\n}\n");
        Assert.Equal("say \"hi\"", root.Child("a")!.Get("K"));
        root.Child("A")!.GetOrAddChild("B").Set("x", "2");
        var again = Vdf.Parse(Vdf.Serialize(root));
        Assert.Equal("2", again.Child("A")!.Child("B")!.Get("x"));
        Assert.Equal("say \"hi\"", again.Child("A")!.Get("k"));
    }

    [Fact]
    public void Manifest_SkipsToolsAndIncomplete()
    {
        string apps = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(apps, "common", "Game"));
        string Acf(string id, string name, string flags) =>
            $"\"AppState\" {{ \"appid\" \"{id}\" \"name\" \"{name}\" \"installdir\" \"Game\" \"StateFlags\" \"{flags}\" }}";
        Assert.Equal("Game X", GameLibrary.ParseManifest(Acf("1", "Game X", "4"), apps)!.Name);
        Assert.Null(GameLibrary.ParseManifest(Acf("2", "Game X", "1026"), apps)); // baixando
        Assert.Null(GameLibrary.ParseManifest(Acf("3", "Proton 9.0", "4"), apps));
    }

    [Fact]
    public void BuildArgs_PerEngine()
    {
        Assert.Equal("-windowed -noborder -w 1920 -h 1080 -freq 144 -novid",
            GameLauncher.BuildArgs(new GameProfile { WinMode = "borderless", Res = "1920x1080", Hz = 144, Extra = " -novid " }));
        Assert.Equal("-popupwindow -screen-width 1280 -screen-height 720",
            GameLauncher.BuildArgs(new GameProfile { Style = "unity", WinMode = "borderless", Res = "1280x720" }));
        Assert.Equal("-windowed -ResX=2560 -ResY=1440",
            GameLauncher.BuildArgs(new GameProfile { Style = "unreal", WinMode = "window", Res = "2560x1440", Hz = 144 }));
    }

    [Fact]
    public void LaunchLine_WrappersBeforeCommand()
    {
        Assert.Equal($"MANGOHUD_CONFIG=\"{MangoHudFrames.LogConfig}\" mangohud gamemoderun %command% -novid",
            GameLauncher.LaunchLine(new GameProfile { GameMode = true, Extra = "-novid" }));
        Assert.Equal("-fullscreen", GameLauncher.LaunchLine(new GameProfile { MangoHud = false, WinMode = "full" }));
        Assert.Equal("", GameLauncher.LaunchLine(new GameProfile { MangoHud = false }));
    }
}
