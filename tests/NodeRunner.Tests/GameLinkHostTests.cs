using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class GameLinkHostTests
{
    private const string State = """
        {"v":2,"players":[{"id":3,"name":"Mapper","x":-75.5,"y":-818.25,"z":326.2,"h":90,
         "cx":-70,"cy":-820,"cz":328,"rx":-10,"ry":0,"rz":0}]}
        """;

    [Fact]
    public async Task Poll_brings_players_and_takes_commands()
    {
        using var host = new GameLinkHost();
        Assert.True(host.Start(0));
        Assert.False(host.IsConnected);
        host.Teleport(3, new Vector3(1f, 2f, 3f), 45f);

        using var http = new HttpClient();
        var reply = await http.PostAsync($"http://127.0.0.1:{host.Port}{GameLinkHost.Route}",
            new StringContent(State, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        using var json = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
        var command = Assert.Single(json.RootElement.GetProperty("commands").EnumerateArray());
        Assert.Equal("teleport", command.GetProperty("type").GetString());
        Assert.Equal(3, command.GetProperty("player").GetInt32());
        Assert.Equal(45f, command.GetProperty("h").GetSingle());

        Assert.True(host.IsConnected);
        var player = Assert.Single(host.Players);
        Assert.Equal("Mapper", player.Name);
        Assert.Equal(new Vector3(-75.5f, -818.25f, 326.2f), player.Position);

        // Sent once only.
        var again = await http.PostAsync($"http://127.0.0.1:{host.Port}{GameLinkHost.Route}", new StringContent("{}"));
        using var second = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Empty(second.RootElement.GetProperty("commands").EnumerateArray());
        Assert.Empty(host.Players);
    }

    [Fact]
    public async Task Web_pages_are_refused()
    {
        using var host = new GameLinkHost();
        Assert.True(host.Start(0));
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{host.Port}{GameLinkHost.Route}")
        {
            Content = new StringContent(State),
        };
        request.Headers.Add("Origin", "https://example.com");
        var reply = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.False(host.IsConnected);
    }

    [Fact]
    public void Game_and_editor_angles_match()
    {
        // Game camera looking north, level: editor yaw = 90° (east = 0).
        var north = new GameLinkPlayer(1, "", Vector3.Zero, 0f, Vector3.Zero, new Vector3(0f, 0f, 0f));
        Assert.Equal(MathF.PI / 2f, north.CameraYaw, 4);
        // Looking west (game yaw 90, turning left).
        var west = north with { CameraRotation = new Vector3(-20f, 0f, 90f) };
        Assert.Equal(MathF.PI, west.CameraYaw, 4);
        Assert.Equal(-20f * MathF.PI / 180f, west.CameraPitch, 4);
        // And back to a game heading.
        Assert.Equal(0f, GameLinkHost.HeadingFromYaw(MathF.PI / 2f), 3);
        Assert.Equal(90f, GameLinkHost.HeadingFromYaw(MathF.PI), 3);
        Assert.Equal(270f, GameLinkHost.HeadingFromYaw(0f), 3);
    }

    [Fact]
    public void Odd_bodies_give_no_players()
    {
        Assert.Empty(GameLinkHost.ReadPlayers("not json"));
        Assert.Empty(GameLinkHost.ReadPlayers("{\"players\":{}}"));
        Assert.Single(GameLinkHost.ReadPlayers("{\"players\":[{\"id\":1}]}"));
    }
}
