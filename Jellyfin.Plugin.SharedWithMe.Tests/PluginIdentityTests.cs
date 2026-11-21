using System;
using System.Runtime.CompilerServices;
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
