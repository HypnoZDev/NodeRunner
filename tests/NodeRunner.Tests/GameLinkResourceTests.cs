using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class GameLinkResourceTests
{
    [Theory]
    [InlineData("endpoint_add_tcp \"0.0.0.0:30120\"\nensure mapmanager\n")]
    [InlineData("ensure chat\r\nensure spawnmanager\r\n")]
    [InlineData("")]
    public void Config_block_is_added_once_and_removed_cleanly(string config)
    {
        string added = GameLinkResource.AddToConfig(config);
        Assert.True(GameLinkResource.HasConfigBlock(added));
        Assert.Contains($"ensure {GameLinkResource.ResourceName}", added);
        Assert.StartsWith(config, added);
        Assert.Equal(added, GameLinkResource.AddToConfig(added));
        Assert.Equal(config, GameLinkResource.RemoveFromConfig(added));
    }

    [Fact]
    public void Block_in_the_middle_keeps_the_lines_after_it()
    {
        string added = GameLinkResource.AddToConfig("ensure chat\n");
        string config = added + "ensure later\n";
        Assert.Equal("ensure chat\n\nensure later\n", GameLinkResource.RemoveFromConfig(config));
    }

    [Fact]
    public void Map_resources_are_found_or_put_next_to_the_link()
    {
        string root = Path.Combine(Path.GetTempPath(), "nr-link-" + Guid.NewGuid().ToString("N"));
        string server = Path.Combine(root, "server");
        try
        {
            string mine = Path.Combine(server, "resources", "[maps]", "[city]", "my_map");
            Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(mine, "fxmanifest.lua"), "fx_version 'cerulean'");
            File.WriteAllText(Path.Combine(server, "server.cfg"), "ensure chat\n");
            GameLinkResource.Install(server);

            Assert.Equal(mine, GameLinkResource.MapFolder(server, "my_map"));
            GameLinkResource.StartWithServer(server, mine, "my_map");
            Assert.Empty(GameLinkResource.ConfigMaps(File.ReadAllText(Path.Combine(server, "server.cfg"))));

            string fresh = GameLinkResource.MapFolder(server, "new_map");
            Assert.Equal(Path.Combine(server, "resources", GameLinkResource.CategoryFolder, "new_map"), fresh);
            GameLinkResource.StartWithServer(server, fresh, "new_map");
            GameLinkResource.StartWithServer(server, fresh, "new_map");
            string config = File.ReadAllText(Path.Combine(server, "server.cfg"));
            Assert.Equal(["new_map"], GameLinkResource.ConfigMaps(config));
            Assert.Contains("command.ensure allow", config);

            // Updating the link keeps the maps it starts.
            GameLinkResource.Install(server);
            Assert.Equal(["new_map"], GameLinkResource.ConfigMaps(File.ReadAllText(Path.Combine(server, "server.cfg"))));
            GameLinkResource.Uninstall(server);
            Assert.Equal("ensure chat\n", File.ReadAllText(Path.Combine(server, "server.cfg")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Installs_finds_and_uninstalls_on_a_server_folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "nr-link-" + Guid.NewGuid().ToString("N"));
        string server = Path.Combine(root, "txData", "default");
        try
        {
            Directory.CreateDirectory(Path.Combine(server, "resources", "[maps]", "my_map", "stream"));
            File.WriteAllText(Path.Combine(server, "server.cfg"), "ensure chat\n");

            Assert.Equal(server, GameLinkResource.FindServerFolder(Path.Combine(server, "resources", "[maps]", "my_map")));
            Assert.Equal(server, GameLinkResource.FindServerFolder(root));
            Assert.Equal([server], GameLinkResource.Search([root]));
            Assert.Equal(GameLinkResource.State.NotInstalled, GameLinkResource.StateOf(server));

            GameLinkResource.Install(server);
            Assert.Equal(GameLinkResource.State.Installed, GameLinkResource.StateOf(server));
            Assert.Equal(GameLinkResource.Version, GameLinkResource.InstalledVersion(server));
            Assert.Equal("ensure chat\n", File.ReadAllText(Path.Combine(server, "server.cfg" + GameLinkResource.ConfigBackupSuffix)));

            File.WriteAllText(Path.Combine(GameLinkResource.ResourceFolder(server), "mine.txt"), "kept");
            GameLinkResource.Uninstall(server);
            Assert.Equal(GameLinkResource.State.NotInstalled, GameLinkResource.StateOf(server));
            Assert.Equal("ensure chat\n", File.ReadAllText(Path.Combine(server, "server.cfg")));
            Assert.True(File.Exists(Path.Combine(GameLinkResource.ResourceFolder(server), "mine.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
