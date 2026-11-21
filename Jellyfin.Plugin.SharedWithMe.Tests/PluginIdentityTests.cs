using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public class PluginIdentityTests
{
    [Fact]
    public void Configuration_defaults_are_safe()
    {
        var config = new PluginConfiguration();

        Assert.Equal(string.Empty, config.ShareRoot);
        Assert.Equal(WhoMayShare.Everyone, config.WhoMayShare);
        Assert.Empty(config.AllowedSharers);
        Assert.Equal(0, config.TvShareDays);
    }

    // 0.2, B: how long a share made from a TV playlist lasts, through both forms the settings travel in, held to 0..366.
    [Fact]
    public void TvShareDays_survives_a_save_and_load_and_is_held_to_0_to_366()
    {
        var config = new PluginConfiguration { TvShareDays = 7 };
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var xml = new StringWriter();
        serializer.Serialize(xml, config);
        Assert.Equal(7, ((PluginConfiguration)serializer.Deserialize(new StringReader(xml.ToString()))!).TvShareDays);

        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        Assert.Equal(7, JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(config, json), json)!.TvShareDays);

        Assert.Equal(0, new PluginConfiguration { TvShareDays = -5 }.TvShareDays);
        Assert.Equal(366, new PluginConfiguration { TvShareDays = 366 }.TvShareDays);
        Assert.Equal(366, new PluginConfiguration { TvShareDays = 1000 }.TvShareDays);
        Assert.Equal(366, JsonSerializer.Deserialize<PluginConfiguration>("{\"TvShareDays\":9999}", json)!.TvShareDays);
        var edited = xml.ToString().Replace("<TvShareDays>7</TvShareDays>", "<TvShareDays>-3</TvShareDays>", StringComparison.Ordinal);
        Assert.Equal(0, ((PluginConfiguration)serializer.Deserialize(new StringReader(edited))!).TvShareDays);
    }

    // The server keeps the settings as XML (System.Xml.Serialization) and the dashboard sends them as JSON with enums as
    // strings.
    [Fact]
    public void Chosen_sharers_survive_a_save_and_load()
    {
        var kid = Guid.NewGuid();
        var config = new PluginConfiguration { WhoMayShare = WhoMayShare.SelectedProfiles, AllowedSharers = new[] { kid } };

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var xml = new StringWriter();
        serializer.Serialize(xml, config);
        var back = (PluginConfiguration)serializer.Deserialize(new StringReader(xml.ToString()))!;
        Assert.Equal(WhoMayShare.SelectedProfiles, back.WhoMayShare);
        Assert.Equal(new[] { kid }, back.AllowedSharers);

        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var text = JsonSerializer.Serialize(config, json);
        Assert.Contains("\"SelectedProfiles\"", text, StringComparison.Ordinal);
        var fromJson = JsonSerializer.Deserialize<PluginConfiguration>(text, json)!;
        Assert.Equal(WhoMayShare.SelectedProfiles, fromJson.WhoMayShare);
        Assert.Equal(new[] { kid }, fromJson.AllowedSharers);
    }

    // A settings file written by 0.1 has no AllowedSharers: it loads exactly as before, with an empty list.
    [Fact]
    public void A_settings_file_from_before_chosen_sharers_loads_as_before()
    {
        const string old = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n"
            + "  <ShareRoot>/srv/shares</ShareRoot>\n"
            + "  <WhoMayShare>UncappedOnly</WhoMayShare>\n"
            + "  <TvSharingPlaylists>false</TvSharingPlaylists>\n"
            + "</PluginConfiguration>\n";

        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(new StringReader(old))!;

        Assert.Equal("/srv/shares", config.ShareRoot);
        Assert.Equal(WhoMayShare.UncappedOnly, config.WhoMayShare);
        Assert.False(config.TvSharingPlaylists);
        Assert.NotNull(config.AllowedSharers);
        Assert.Empty(config.AllowedSharers);
        Assert.Equal(0, config.TvShareDays); // no TvShareDays (before 0.2, B): TV shares last until removed, as before
    }

    // The id is how servers and repositories know the plugin: it must never change once published.
    [Fact]
    public void The_plugin_id_and_name_are_fixed()
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

        Assert.Equal(Guid.Parse("4069397e-cf0d-460a-9a00-e5175c137516"), plugin.Id);
        Assert.Equal("4069397e-cf0d-460a-9a00-e5175c137516", Plugin.PluginGuid);
        Assert.Equal("Shared With Me", plugin.Name);
    }

    [Fact]
    public void Uninstalling_runs_the_teardown_and_never_lets_it_throw()
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var ran = 0;
        plugin.Uninstalling = () =>
        {
            ran++;
            throw new InvalidOperationException("boom");
        };

        plugin.OnUninstalling();

        Assert.Equal(1, ran);
    }

    [Fact]
    public void Uninstalling_before_the_service_started_warns_that_no_clean_up_ran()
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var err = new System.IO.StringWriter();
        var old = Console.Error;
        Console.SetError(err);
        try
        {
            plugin.OnUninstalling();
        }
        finally
        {
            Console.SetError(old);
        }

        Assert.Contains("no clean-up ran", err.ToString(), StringComparison.Ordinal);
    }
}
