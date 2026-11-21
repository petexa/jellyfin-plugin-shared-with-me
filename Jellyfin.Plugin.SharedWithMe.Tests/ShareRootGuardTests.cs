using Jellyfin.Plugin.SharedWithMe.Configuration;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class ShareRootGuardTests
{
    private const string Data = "/config/data";

    [Fact]
    public void A_changed_share_root_is_refused_while_in_charge_and_other_fields_still_apply()
    {
        var current = new PluginConfiguration { ShareRoot = string.Empty, WhoMayShare = WhoMayShare.Everyone };
        var next = new PluginConfiguration { ShareRoot = "/elsewhere", WhoMayShare = WhoMayShare.AdminsOnly };

        Assert.True(ShareRootGuard.KeepWhileInCharge(current, next, inCharge: true, Data));

        Assert.Equal(string.Empty, next.ShareRoot);
        Assert.Equal(WhoMayShare.AdminsOnly, next.WhoMayShare);
    }

    [Fact]
    public void A_changed_share_root_is_accepted_when_not_in_charge()
    {
        var current = new PluginConfiguration { ShareRoot = string.Empty };
        var next = new PluginConfiguration { ShareRoot = "/elsewhere" };

        Assert.False(ShareRootGuard.KeepWhileInCharge(current, next, inCharge: false, Data));
        Assert.Equal("/elsewhere", next.ShareRoot);
    }

    [Fact]
    public void The_same_folder_written_differently_is_not_a_change()
    {
        var current = new PluginConfiguration { ShareRoot = string.Empty };
        var next = new PluginConfiguration { ShareRoot = "/config/data/shared-with-me/" };

        Assert.False(ShareRootGuard.KeepWhileInCharge(current, next, inCharge: true, Data));
        Assert.Equal("/config/data/shared-with-me/", next.ShareRoot);
    }
}
