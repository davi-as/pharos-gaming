namespace PharosGaming.Tests;

public class LimiterTests
{
    [Fact]
    public void KeyFor_MatchesMangoHudNaming()
    {
        Assert.Equal("wine-eldenring", FrameLimiter.KeyFor("/x/files/bin/wine64-preloader", "eldenring.exe", Array.Empty<string>()));
        // comm cortado em 15 caracteres: o MangoHud vai pela linha de comando
        Assert.Equal("wine-Cyberpunk2077", FrameLimiter.KeyFor("/x/bin/wine-preloader", "Cyberpunk2077.e", new[] { "", @"Z:\games\Cyberpunk2077.exe" }));
        Assert.Equal("retroarch", FrameLimiter.KeyFor("/usr/bin/retroarch", "retroarch", new[] { "retroarch" }));
    }

    [Fact]
    public void Pick_PrefersTheGameWindowOverWineExplorer()
    {
        var games = new[] { new RunningGame(10, "wine-explorer"), new RunningGame(11, "wine-launcher"), new RunningGame(12, "wine-Hades") };
        Assert.Equal("wine-Hades", FrameLimiter.Pick(games, new HashSet<int> { 12 })!.Key);
        Assert.Equal("wine-launcher", FrameLimiter.Pick(games, new HashSet<int>())!.Key); // sem janelas: nunca o explorer
        Assert.Null(FrameLimiter.Pick(new[] { new RunningGame(10, "wine-explorer") }, new HashSet<int>()));
    }

    [Fact]
    public void Build_ReplacesLimitLinesAndKeepsTheRest()
    {
        var conf = FrameLimiter.Build("wine-eldenring", 82.5,
            new[] { "# Gerado pelo PHAROS GAMING (aba Overlay).", "toggle_hud=Control_L+Shift_L+Q", "no_display=1", "fps_limit=30", "output_folder=/tmp" }).Split('\n');
        Assert.StartsWith("# Gerado pelo PHAROS GAMING (Limitador de FPS).", conf[0]);
        Assert.Single(conf, l => l.StartsWith("# Gerado"));
        Assert.Contains("toggle_hud=Control_L+Shift_L+Q", conf);
        Assert.Contains("no_display=1", conf);
        Assert.Contains("fps_limit=82.5,0", conf);
        Assert.Contains("fps_limit_method=early", conf);
        Assert.Single(conf, l => l.StartsWith("fps_limit="));
        Assert.Single(conf, l => l.StartsWith("output_folder="));
        Assert.Contains("fps_limit=0", FrameLimiter.Build("x", 0, Array.Empty<string>()).Split('\n'));
    }

    [Fact]
    public void Candidates_DivideTheRefreshRate()
    {
        Assert.Equal(new[] { 165, 82.5, 55, 41.25, 33 }, FrameLimiter.Candidates(165, vrr: false));
        Assert.Equal(new[] { 60.0 }, FrameLimiter.Candidates(60, vrr: false).Take(1));
        Assert.DoesNotContain(165.0, FrameLimiter.Candidates(165, vrr: true)); // com VRR fica abaixo da taxa
        Assert.Equal(55, FrameLimiter.Suggest(FrameLimiter.Candidates(165, false), minFps: 70));
        Assert.Null(FrameLimiter.Suggest(new[] { 60.0 }, minFps: 40));
    }

    [Fact]
    public void Judge_FlagsUnstablePacingAndMissedLimit()
    {
        Assert.Null(FrameLimiter.Judge(new[] { 16.7, 16.7 }, 60));
        Assert.True(FrameLimiter.Judge(Enumerable.Repeat(16.67, 20).ToList(), 60)!.Good);
        Assert.Equal("Abaixo do limite", FrameLimiter.Judge(Enumerable.Repeat(20.0, 20).ToList(), 60)!.Title);
        var jittery = Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? 10.0 : 22.0).ToList();
        Assert.Equal("Ritmo irregular", FrameLimiter.Judge(jittery, 0)!.Title);
    }
}

public class KnownGameTests
{
    [Fact]
    public void SteamAppId_FromEnviron()
    {
        Assert.Equal("1245620", FrameLimiter.SteamAppId("HOME=/h\0SteamAppId=1245620\0MANGOHUD=1\0"));
        Assert.Equal("1145360", FrameLimiter.SteamAppId("SteamGameId=1145360\0"));
        Assert.Null(FrameLimiter.SteamAppId("SteamAppId=0\0MANGOHUD=1\0")); // a Steam usa 0 fora de jogo
        Assert.Null(FrameLimiter.SteamAppId("MANGOHUD=1\0"));
    }

    [Fact]
    public void Scan_AddsHydraGamesSeenRunning()
    {
        var known = new Dictionary<string, KnownGame>
        {
            ["wine-pcsx2"] = new() { LastSeen = DateTime.Now },
            ["wine-explorer"] = new() { LastSeen = DateTime.Now },
            ["wine-eldenring"] = new() { SteamAppId = "999999999", LastSeen = DateTime.Now }, // da Steam: não vira item do Hydra
        };
        var list = GameLibrary.Scan(Array.Empty<CustomGame>(), known).Where(g => g.Store == "Hydra").ToList();
        Assert.Equal(new[] { "pcsx2" }, list.Select(g => g.Name));
        Assert.Equal("wine-pcsx2", list[0].MangoKey);
    }
}
