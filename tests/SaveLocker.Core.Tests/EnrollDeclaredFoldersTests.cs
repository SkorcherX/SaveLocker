using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SaveLocker.Agent;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// The server's own build output, run as a real process on a free port with its own state, for tests
/// whose subject is the agent talking to a real server. The test project references the server
/// project for build order only, so its output is always there.
/// </summary>
public sealed class ServerProcess : IDisposable
{
    private readonly Process _process;
    public string State { get; } = Path.Combine(Path.GetTempPath(), "savelocker-server-" + Guid.NewGuid().ToString("N"));
    public string Url { get; }

    public ServerProcess()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Url = $"http://127.0.0.1:{port}";

        Directory.CreateDirectory(State);
        var psi = new ProcessStartInfo("dotnet", $"\"{ServerDll()}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["ASPNETCORE_URLS"] = Url;
        psi.Environment["Storage__DbPath"] = Path.Combine(State, "savelocker.db");
        psi.Environment["Storage__ArchiveRoot"] = Path.Combine(State, "archives");
        psi.Environment["Backup__Enabled"] = "false";
        _process = Process.Start(psi)!;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            try
            {
                if (http.GetAsync(Url + "/api/admin/status").Result.IsSuccessStatusCode) return;
            }
            catch (AggregateException) { }
            if (_process.HasExited) break;
            Thread.Sleep(250);
        }
        Dispose();
        throw new InvalidOperationException($"The server did not start on {Url}.");
    }

    /// <summary>src/Server/bin/&lt;same configuration as this test build&gt;/net10.0/SaveLocker.Server.dll.</summary>
    private static string ServerDll()
    {
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SaveLocker.sln"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Could not find the repository root.");
        return Path.Combine(dir.FullName, "src", "Server", "bin", config, "net10.0", "SaveLocker.Server.dll");
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _process.Dispose();
        try { Directory.Delete(State, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Phase 5 of multiple save paths: a scanner that KNOWS a game keeps several folders (an emulator's
/// saves and save states) declares them, and enrollment adopts them without asking — on the machine
/// that creates the game and on the next one — while a game the server already holds with a
/// different definition is left alone.
/// </summary>
public sealed class EnrollDeclaredFoldersTests : IClassFixture<ServerProcess>, IDisposable
{
    private readonly ServerProcess _server;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "savelocker-declared-" + Guid.NewGuid().ToString("N"));

    public EnrollDeclaredFoldersTests(ServerProcess server)
    {
        _server = server;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<AgentConfig> Machine(string name)
    {
        var config = AgentConfig.Load(Path.Combine(_dir, name, "config.json"));
        config.ServerUrl = _server.Url;
        var reg = await new ApiClient(config.ServerUrl, null).RegisterAsync(name);
        config.ApiKey = reg.ApiKey;
        config.MachineId = reg.MachineId;
        config.MachineName = name;
        config.Save();
        return config;
    }

    private string Dir(string machine, string name)
    {
        var d = Path.Combine(_dir, machine, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "file.srm"), name);
        return d;
    }

    /// <summary>A RetroArch-shaped candidate: one ROM's save and save states in folders other ROMs share.</summary>
    private ScanCandidate Candidate(string game, string machine, string statesScope = "Rom.state*") => new(
        game, Dir(machine, "saves"), ScanSource.SaveRoot, false,
        IncludeGlobs: new[] { "Rom.srm" },
        ExtraSaveDirs: new[] { new DeclaredSavePath("states", Dir(machine, "states"), new[] { statesScope }) });

    [Fact]
    public async Task Declared_folders_are_adopted_on_the_first_machine_and_the_next()
    {
        var game = "Declared-" + Guid.NewGuid().ToString("N")[..8];
        var a = await Machine("a-" + game);
        var b = await Machine("b-" + game);

        var first = await Enroller.EnrollAsync(a, new[] { Candidate(game, "a") }, new[] { 0 });
        var second = await Enroller.EnrollAsync(b, new[] { Candidate(game, "b") }, new[] { 0 });

        Assert.Equal((1, 0), first);
        Assert.Equal((1, 0), second);
        foreach (var (config, machine) in new[] { (a, "a"), (b, "b") })
        {
            var tracked = AgentConfig.Load(config.ConfigPath).FindGame(game)!;
            Assert.Equal(new[] { "Rom.srm" }, tracked.IncludeGlobs);
            var states = Assert.Single(tracked.ExtraPaths);
            Assert.Equal("states", states.Key);
            Assert.Equal(Path.Combine(_dir, machine, "states"), states.Directory);
            Assert.Equal(new[] { "Rom.state*" }, states.IncludeGlobs);
        }

        // The server holds one game with both scopes, and each machine's own folder for it.
        var seenByB = (await ApiClient.For(b).ListGamesAsync()).Single(g => g.Name == game);
        Assert.Equal(new[] { "Rom.srm" }, seenByB.IncludeGlobs);
        var serverStates = Assert.Single(seenByB.ExtraPaths!);
        Assert.Equal(new[] { "Rom.state*" }, serverStates.IncludeGlobs);
        Assert.Equal(Path.Combine(_dir, "b", "states"), serverStates.MachinePath);
    }

    [Fact]
    public async Task A_game_the_server_defines_differently_is_skipped()
    {
        var game = "Differs-" + Guid.NewGuid().ToString("N")[..8];
        var a = await Machine("a-" + game);
        var c = await Machine("c-" + game);
        await Enroller.EnrollAsync(a, new[] { Candidate(game, "a") }, new[] { 0 });

        var result = await Enroller.EnrollAsync(c, new[] { Candidate(game, "c", statesScope: "*.state") }, new[] { 0 });

        Assert.Equal((0, 1), result);
        Assert.Null(AgentConfig.Load(c.ConfigPath).FindGame(game));
    }

    [Fact]
    public async Task A_declared_folder_inside_the_primary_one_is_refused_before_the_game_exists()
    {
        var game = "Nested-" + Guid.NewGuid().ToString("N")[..8];
        var a = await Machine("a-" + game);
        var saves = Dir("a", "nested-saves");
        var candidate = new ScanCandidate(game, saves, ScanSource.SaveRoot, false,
            ExtraSaveDirs: new[] { new DeclaredSavePath("states", Dir("a", Path.Combine("nested-saves", "states"))) });

        var result = await Enroller.EnrollAsync(a, new[] { candidate }, new[] { 0 });

        Assert.Equal((0, 1), result);
        Assert.DoesNotContain(await ApiClient.For(a).ListGamesAsync(), g => g.Name == game);
    }

    /// <summary>A manifest-shaped candidate: one save folder, two more locations "Also found".</summary>
    private ScanCandidate WithAlsoFound(string game, string machine) => new(
        game, Dir(machine, "docs"), ScanSource.SteamInstalled, false,
        AlternateSaveDirs: new[]
        {
            new DeclaredSavePath("appdata", Dir(machine, "appdata")),
            new DeclaredSavePath("config", Dir(machine, "config")),
        });

    [Fact]
    public async Task Only_the_ticked_also_found_folders_are_added()
    {
        var game = "AlsoFound-" + Guid.NewGuid().ToString("N")[..8];
        var a = await Machine("a-" + game);

        var result = await Enroller.EnrollAsync(a, new[] { WithAlsoFound(game, "a") }, new[] { 0 },
            alsoSync: new Dictionary<int, string[]> { [0] = new[] { Path.Combine(_dir, "a", "appdata") } });

        Assert.Equal((1, 0), result);
        var tracked = AgentConfig.Load(a.ConfigPath).FindGame(game)!;
        var appdata = Assert.Single(tracked.ExtraPaths);
        Assert.Equal("appdata", appdata.Key);
        Assert.Equal(Path.Combine(_dir, "a", "appdata"), appdata.Directory);

        var server = (await ApiClient.For(a).ListGamesAsync()).Single(g => g.Name == game);
        var serverPath = Assert.Single(server.ExtraPaths!);
        Assert.Equal("appdata", serverPath.Key);
        Assert.Equal(Path.Combine(_dir, "a", "appdata"), serverPath.MachinePath);
    }

    [Fact]
    public async Task Nothing_ticked_enrolls_the_primary_folder_alone()
    {
        var game = "NoneTicked-" + Guid.NewGuid().ToString("N")[..8];
        var a = await Machine("a-" + game);

        Assert.Equal((1, 0), await Enroller.EnrollAsync(a, new[] { WithAlsoFound(game, "a") }, new[] { 0 }));

        Assert.Empty(AgentConfig.Load(a.ConfigPath).FindGame(game)!.ExtraPaths);
        Assert.Empty((await ApiClient.For(a).ListGamesAsync()).Single(g => g.Name == game).ExtraPaths ?? []);
    }

    [Fact]
    public async Task A_folder_the_fleet_already_has_is_joined_not_added_twice()
    {
        var game = "AlsoJoined-" + Guid.NewGuid().ToString("N")[..8];
        var a = await Machine("a-" + game);
        var b = await Machine("b-" + game);
        // The same manifest location on both machines — one folder, so one template wherever a token describes it.
        var appdata = Dir("shared", "appdata");
        ScanCandidate Shared(string machine) => new(game, Dir(machine, "docs"), ScanSource.SteamInstalled, false,
            AlternateSaveDirs: new[] { new DeclaredSavePath("appdata", appdata) });
        var ticks = new Dictionary<int, string[]> { [0] = new[] { appdata } };
        await Enroller.EnrollAsync(a, new[] { Shared("a") }, new[] { 0 }, alsoSync: ticks);

        var second = await Enroller.EnrollAsync(b, new[] { Shared("b") }, new[] { 0 }, alsoSync: ticks);

        // Joined, not skipped; the folder stays one folder (the poller maps b's copy of it).
        Assert.Equal((1, 0), second);
        var server = (await ApiClient.For(b).ListGamesAsync()).Single(g => g.Name == game);
        Assert.Equal("appdata", Assert.Single(server.ExtraPaths!).Key);
    }

    [Theory]
    // The same template is the same folder, whatever its key.
    [InlineData("appdata", "<winAppData>/Game", "config", "<winAppData>/Game", true)]
    [InlineData("appdata", "<winAppData>/Game", "appdata", "<WINAPPDATA>/Game", true)]
    // A key match with nothing to compare (a template missing on either side): the key came from the
    // same manifest template on every machine, so it is that folder.
    [InlineData("appdata", null, "appdata", "<winAppData>/Game", true)]
    [InlineData("appdata", "<winAppData>/Game", "appdata", null, true)]
    // One key, two different folders: someone added an unrelated "appdata" — this one is another folder.
    [InlineData("appdata", "<winDocuments>/Elsewhere", "appdata", "<winAppData>/Game", false)]
    [InlineData("config", "<winAppData>/Game/Config", "appdata", "<winAppData>/Game", false)]
    public void The_fleet_has_an_also_found_folder_by_template_or_by_a_key_nothing_contradicts(
        string serverKey, string? serverTemplate, string key, string? template, bool joins)
    {
        var server = new[] { new SavePathDto(serverKey, null, serverTemplate, null) };
        Assert.Equal(joins, Enroller.FleetHasFolder(server, key, template));
    }
}
