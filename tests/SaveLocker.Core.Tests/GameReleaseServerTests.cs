using SaveLocker.Agent;
using SaveLocker.Shared;
using Xunit;

namespace SaveLocker.Agent.Tests;

/// <summary>
/// A console game's release on a real server: two regions of one game share its name and stay two games, a
/// release joins only its own region's, and a game made before releases were recorded takes the first one.
/// </summary>
public sealed class GameReleaseServerTests(ServerProcess server) : IClassFixture<ServerProcess>
{
    private const string WarriorWithin = "Prince of Persia - Warrior Within";

    private async Task<ApiClient> Machine(string name)
    {
        var reg = await new ApiClient(server.Url, null).RegisterAsync(name);
        return new ApiClient(server.Url, reg.ApiKey);
    }

    [Fact]
    public async Task Two_regions_of_one_name_are_two_games_and_each_release_joins_its_own()
    {
        var deck = await Machine("deck-" + Guid.NewGuid().ToString("N")[..6]);
        var pc = await Machine("pc-" + Guid.NewGuid().ToString("N")[..6]);
        var usa = new GameReleaseDto("PlayStation 2", "USA", ["SLUS-21022"], ["En", "Fr", "Es"], "Redump");
        var europe = usa with { Region = "Europe, Australia", GameIds = ["SLES-52822"] };

        var a = await deck.CreateGameAsync(new CreateGameRequest(WarriorWithin, null, null,
            IncludeGlobs: ["BASLUS-21022*/**"], Release: usa));
        var b = await pc.CreateGameAsync(new CreateGameRequest(WarriorWithin, null, null,
            IncludeGlobs: ["BESLES-52822*/**"], Release: europe));
        var again = await pc.CreateGameAsync(new CreateGameRequest(WarriorWithin, null, null,
            IncludeGlobs: ["BASLUS-21022*/**"], Release: usa));

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Id, again.Id);
        Assert.Equal((WarriorWithin, "Europe, Australia"), (b.Name, b.Release?.Region));
        var listed = (await pc.ListGamesAsync()).Where(g => g.Name == WarriorWithin).ToList();
        Assert.Equal(new[] { "Europe, Australia", "USA" }, listed.Select(g => g.Release?.Region).Order());
        var stored = listed.Single(g => g.Id == a.Id).Release!;
        Assert.Equal(("PlayStation 2", "Redump"), (stored.Console, stored.NamedBy));
        Assert.Equal(new[] { "SLUS-21022" }, stored.GameIds);
        Assert.Equal(new[] { "En", "Fr", "Es" }, stored.Languages);
    }

    [Fact]
    public async Task A_game_with_no_release_takes_the_first_one_and_a_request_without_one_joins_any()
    {
        var api = await Machine("pc-" + Guid.NewGuid().ToString("N")[..6]);
        var name = "Kingdom Hearts II " + Guid.NewGuid().ToString("N")[..6];
        var old = await api.CreateGameAsync(new CreateGameRequest(name, null, null));
        Assert.Null(old.Release);

        var joined = await api.CreateGameAsync(new CreateGameRequest(name, null, null,
            Release: new GameReleaseDto("PlayStation 2", "USA", ["SLUS-21005"])));
        Assert.Equal(old.Id, joined.Id);
        Assert.Equal("USA", joined.Release?.Region);

        Assert.Equal(old.Id, (await api.CreateGameAsync(new CreateGameRequest(name, null, null))).Id);
    }

    [Fact]
    public async Task An_oversized_release_is_refused()
    {
        var api = await Machine("pc-" + Guid.NewGuid().ToString("N")[..6]);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => api.CreateGameAsync(new CreateGameRequest(
            "Too long " + Guid.NewGuid().ToString("N")[..6], null, null,
            Release: new GameReleaseDto("PlayStation 2", new string('x', GameReleaseDto.MaxLength + 1)))));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.StatusCode);
    }
}
