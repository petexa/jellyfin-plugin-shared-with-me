using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class ShareEligibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-el-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServerGateway _gw = new();
    private readonly ShareStore _store;
    private WhoMayShare _who = WhoMayShare.Everyone;
    private Guid[]? _allowed = Array.Empty<Guid>();

    private readonly Guid _sharer = Guid.NewGuid();
    private readonly Guid _recipient = Guid.NewGuid();
    private readonly Guid _item = Guid.NewGuid();
    private readonly Guid _lib = Guid.NewGuid();

    public ShareEligibilityTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ShareStore(_dir);
        AddUser(_sharer, "Adult", cap: null);
        AddUser(_recipient, "Kid1", cap: 12, sub: 1);
        _gw.Ratings.AddRange(new[] { new RatingOption("PG", 10, 0), new RatingOption("12", 12, 0), new RatingOption("15", 15, 0), new RatingOption("18", 18, 0) });
        _gw.Items[_item] = new ItemInfo(_item, ShareKind.Movie, "Spike", 2001, "15", new Dictionary<string, string>(), _lib, new List<string>(), "/media/Spike/Spike.mkv");
        _gw.SetVisible(_sharer, _item);
        _gw.RealLibraryIds.Add(_lib);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ShareEligibility Sut() => new(_gw, _store, () => _who, () => _allowed);

    private Decision Check() => Sut().Check(_sharer, _item, _recipient);

    private void AddUser(Guid id, string name, int? cap, int? sub = null, bool admin = false, bool disabled = false, bool hidden = false, string[]? blocked = null, string[]? allowedTags = null) =>
        _gw.Users[id] = new UserInfo(id, name, admin, disabled, hidden, cap, sub, true, new List<Guid>(), blocked ?? Array.Empty<string>(), allowedTags ?? Array.Empty<string>());

    private void SetTags(params string[] tags) =>
        _gw.Items[_item] = _gw.Items[_item] with { Tags = tags };

    private static void AssertRefused(Decision d, Refusal expected)
    {
        Assert.Equal(expected, d.Refusal);
        Assert.False(string.IsNullOrWhiteSpace(d.Message));
        Assert.Null(d.CustomRating);
        Assert.Empty(d.CopyTags);
    }

    private ShareRecord Record(Guid original, Guid recipient, ShareState state = ShareState.Active, string folder = "Spike (2001) [abc]") => new(
        Guid.NewGuid(), original, ShareKind.Movie, "Spike", 2001, new Dictionary<string, string>(), _lib, _sharer, "Adult", recipient, DateTime.UtcNow, state, null, folder);

    // A share library holding a copy of the original, with a record linking them.
    private (Guid Copy, Guid ShareLib) MakeCopy(Guid original, string folder = "Spike (2001) [abc]", ShareState state = ShareState.Active)
    {
        var shareLib = Guid.NewGuid();
        var copy = Guid.NewGuid();
        _store.SetLibrary(_recipient, ShareKind.Movie, shareLib);
        _store.Upsert(Record(original, _recipient, state, folder));
        _gw.Items[copy] = new ItemInfo(copy, ShareKind.Movie, "Spike", 2001, "12", new Dictionary<string, string>(), shareLib, new List<string>(), $"/shares/Kid1/movies/{folder}/Spike.mkv");
        _gw.SetVisible(_sharer, copy); // a copy can only be submitted by someone who can see it
        return (copy, shareLib);
    }

    [Fact]
    public void AllowsAndStampsRatingForCappedRecipient()
    {
        var d = Check();
        Assert.Equal(Refusal.None, d.Refusal);
        Assert.Equal(string.Empty, d.Message);
        Assert.Equal("12", d.CustomRating);
        Assert.Empty(d.CopyTags);
    }

    [Fact]
    public void UncappedRecipientGetsNoCustomRating()
    {
        AddUser(_recipient, "Kid1", cap: null);
        var d = Check();
        Assert.Equal(Refusal.None, d.Refusal);
        Assert.Null(d.CustomRating);
    }

    [Fact]
    public void Rated18ForCap12SubOneGives12()
    {
        _gw.Items[_item] = _gw.Items[_item] with { OfficialRating = "18" };
        var d = Check();
        Assert.Equal(Refusal.None, d.Refusal);
        Assert.Equal("12", d.CustomRating);
    }

    [Fact]
    public void CopyTagsIsFirstAllowedTag()
    {
        AddUser(_recipient, "Kid1", cap: 12, sub: 1, allowedTags: new[] { "kids", "other" });
        var d = Check();
        Assert.Equal(Refusal.None, d.Refusal);
        Assert.Equal(new[] { "kids" }, d.CopyTags);
    }

    [Fact]
    public void NotVisibleToSharer()
    {
        AddUser(_sharer, "Adult", cap: 12);
        _gw.SetVisible(_sharer, _item, false);
        var d = Check();
        AssertRefused(d, Refusal.NotVisibleToSharer);
        Assert.Equal(ShareEligibility.CannotShareMessage, d.Message);
    }

    [Fact]
    public void NotVisibleBeatsBlockedTag()
    {
        _gw.SetVisible(_sharer, _item, false);
        AddUser(_recipient, "Kid1", cap: 12, blocked: new[] { "gore" });
        SetTags("gore");
        AssertRefused(Check(), Refusal.NotVisibleToSharer);
    }

    [Fact]
    public void NotShareableTypeWhenKindNull()
    {
        _gw.Items[_item] = _gw.Items[_item] with { Kind = null };
        AssertRefused(Check(), Refusal.NotShareableType);
    }

    [Fact]
    public void MissingItemIsNotVisibleToSharer()
    {
        _gw.Items.Remove(_item);
        AssertRefused(Check(), Refusal.NotVisibleToSharer);
    }

    [Fact]
    public void RecipientUnknownWhenMissing()
    {
        _gw.Users.Remove(_recipient);
        AssertRefused(Check(), Refusal.RecipientUnknown);
    }

    [Fact]
    public void RecipientUnknownWhenDisabled()
    {
        AddUser(_recipient, "Kid1", cap: 12, disabled: true);
        AssertRefused(Check(), Refusal.RecipientUnknown);
    }

    [Fact]
    public void HiddenRecipientIsStillValid()
    {
        AddUser(_recipient, "Kid1", cap: 12, sub: 1, hidden: true);
        Assert.Equal(Refusal.None, Check().Refusal);
    }

    [Fact]
    public void RecipientIsSharer()
    {
        var d = Sut().Check(_sharer, _item, _sharer);
        AssertRefused(d, Refusal.RecipientIsSharer);
    }

    [Fact]
    public void RecipientAlreadySees()
    {
        _gw.SetVisible(_recipient, _item);
        var d = Check();
        AssertRefused(d, Refusal.RecipientAlreadySees);
        Assert.Contains("Kid1", d.Message);
        Assert.Contains("Spike", d.Message);
    }

    [Fact]
    public void BlockedTagNamesTagCaseInsensitively()
    {
        AddUser(_recipient, "Kid1", cap: 12, blocked: new[] { "GORE" });
        SetTags("Funny", "gore");
        var d = Check();
        AssertRefused(d, Refusal.BlockedTag);
        Assert.Equal("Kid1's settings block Spike.", d.Message); // M1: the tag itself is never named
        Assert.DoesNotContain("gore", d.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoRatingFits()
    {
        AddUser(_recipient, "Kid1", cap: 5);
        AssertRefused(Check(), Refusal.NoRatingFits);
    }

    [Fact]
    public void SharerMissingIsNotAllowed()
    {
        _gw.Users.Remove(_sharer);
        AssertRefused(Check(), Refusal.SharingNotAllowed);
    }

    [Fact]
    public void SharerDisabledIsNotAllowed()
    {
        AddUser(_sharer, "Adult", cap: null, disabled: true);
        AssertRefused(Check(), Refusal.SharingNotAllowed);
    }

    [Fact]
    public void AdminsOnly()
    {
        _who = WhoMayShare.AdminsOnly;
        AssertRefused(Check(), Refusal.SharingNotAllowed);
        AddUser(_sharer, "Adult", cap: null, admin: true);
        Assert.Equal(Refusal.None, Check().Refusal);
    }

    [Fact]
    public void UncappedOnly()
    {
        _who = WhoMayShare.UncappedOnly;
        Assert.Equal(Refusal.None, Check().Refusal);
        AddUser(_sharer, "Adult", cap: 18);
        AssertRefused(Check(), Refusal.SharingNotAllowed);
    }

    [Fact]
    public void EveryoneIncludesCappedAndNonAdmin()
    {
        _who = WhoMayShare.Everyone;
        AddUser(_sharer, "Adult", cap: 18);
        Assert.Equal(Refusal.None, Check().Refusal);
    }

    [Fact]
    public void SelectedProfilesAdminAlwaysMayShareThoughNotListed()
    {
        _who = WhoMayShare.SelectedProfiles;
        AddUser(_sharer, "Adult", cap: null, admin: true);
        Assert.Equal(Refusal.None, Check().Refusal);
        _allowed = null;
        Assert.Equal(Refusal.None, Check().Refusal);
    }

    [Fact]
    public void SelectedProfilesListedProfileMayShareEvenWhenCapped()
    {
        _who = WhoMayShare.SelectedProfiles;
        _allowed = new[] { _sharer };
        AddUser(_sharer, "Adult", cap: 18);
        Assert.Equal(Refusal.None, Check().Refusal);
        Assert.True(Sut().MayShare(_sharer));
    }

    [Fact]
    public void SelectedProfilesUnlistedProfileMayNotShare()
    {
        _who = WhoMayShare.SelectedProfiles;
        AssertRefused(Check(), Refusal.SharingNotAllowed);
        _allowed = new[] { _recipient };
        AssertRefused(Check(), Refusal.SharingNotAllowed);
        Assert.False(Sut().MayShare(_sharer));
        _allowed = null; // a missing list is an empty one
        AssertRefused(Check(), Refusal.SharingNotAllowed);
    }

    [Fact]
    public void SelectedProfilesListedButDisabledMayNotShare()
    {
        _who = WhoMayShare.SelectedProfiles;
        _allowed = new[] { _sharer };
        AddUser(_sharer, "Adult", cap: null, disabled: true);
        AssertRefused(Check(), Refusal.SharingNotAllowed);
        Assert.False(Sut().MayShare(_sharer));
    }

    [Fact]
    public void AllowedSharersCountOnlyForSelectedProfiles()
    {
        _allowed = new[] { _sharer };
        _who = WhoMayShare.AdminsOnly;
        AssertRefused(Check(), Refusal.SharingNotAllowed);
        _who = WhoMayShare.UncappedOnly;
        AddUser(_sharer, "Adult", cap: 18);
        AssertRefused(Check(), Refusal.SharingNotAllowed);
        _who = WhoMayShare.Everyone;
        _allowed = Array.Empty<Guid>();
        Assert.Equal(Refusal.None, Check().Refusal);
    }

    [Fact]
    public void AllowedSharersAreReadOnEveryCheck()
    {
        _who = WhoMayShare.SelectedProfiles;
        var sut = Sut();
        Assert.Equal(Refusal.SharingNotAllowed, sut.Check(_sharer, _item, _recipient).Refusal);
        _allowed = new[] { _sharer };
        Assert.Equal(Refusal.None, sut.Check(_sharer, _item, _recipient).Refusal);
        _allowed = Array.Empty<Guid>();
        Assert.Equal(Refusal.SharingNotAllowed, sut.Check(_sharer, _item, _recipient).Refusal);
    }

    [Fact]
    public void WithoutAListEveryProfileButAdministratorsIsUnlisted()
    {
        AddUser(_sharer, "Adult", cap: null);
        var sut = new ShareEligibility(_gw, _store, () => WhoMayShare.SelectedProfiles);
        Assert.Equal(Refusal.SharingNotAllowed, sut.Check(_sharer, _item, _recipient).Refusal);
        AddUser(_sharer, "Adult", cap: null, admin: true);
        Assert.Equal(Refusal.None, sut.Check(_sharer, _item, _recipient).Refusal);
    }

    [Fact]
    public void WhoMayShareIsReadOnEveryCheck()
    {
        var sut = Sut();
        Assert.Equal(Refusal.None, sut.Check(_sharer, _item, _recipient).Refusal);
        _who = WhoMayShare.AdminsOnly;
        Assert.Equal(Refusal.SharingNotAllowed, sut.Check(_sharer, _item, _recipient).Refusal);
    }

    [Fact]
    public void SharingNotAllowedBeatsNotVisible()
    {
        _who = WhoMayShare.AdminsOnly;
        _gw.SetVisible(_sharer, _item, false);
        Assert.Equal(Refusal.SharingNotAllowed, Check().Refusal);
    }

    [Fact]
    public void AlreadySharedWhenActiveRecordExists()
    {
        _store.Upsert(Record(_item, _recipient));
        var d = Check();
        AssertRefused(d, Refusal.AlreadyShared);
        Assert.Contains("Kid1", d.Message);
    }

    // M1: a share by someone else is answered as "can already watch it" (true, and it names nobody).
    [Fact]
    public void A_share_already_made_by_a_different_sharer_answers_as_recipient_already_sees()
    {
        var other = Guid.NewGuid();
        AddUser(other, "Kid2", cap: null);
        _store.Upsert(Record(_item, _recipient) with { SharerUserId = other, SharerName = "Kid2" });

        var d = Check();

        AssertRefused(d, Refusal.RecipientAlreadySees);
        Assert.Equal("Kid1 can already watch Spike.", d.Message);
        Assert.DoesNotContain("Kid2", d.Message, StringComparison.Ordinal);
    }

    // I7: an uncapped recipient who blocks unrated items of the kind gets the highest rating the server knows as the copy's
    // customrating when the original has none, so the copy counts as rated.
    [Theory]
    [InlineData(ShareKind.Movie, "Movie", "18")]
    [InlineData(ShareKind.Series, "Series", "18")]
    [InlineData(ShareKind.Movie, "Series", null)]
    public void An_unrated_original_for_an_uncapped_recipient_who_blocks_unrated_items_gets_the_highest_rating(ShareKind kind, string blockedKind, string? expected)
    {
        _gw.Users[_recipient] = _gw.Users[_recipient] with { CapScore = null, CapSubScore = null, BlocksUnrated = true, BlockedUnratedKinds = new[] { blockedKind } };
        _gw.Items[_item] = _gw.Items[_item] with { Kind = kind, OfficialRating = null };

        var d = Check();

        Assert.Equal(Refusal.None, d.Refusal);
        Assert.Equal(expected, d.CustomRating);
    }

    [Fact]
    public void A_rated_original_for_an_uncapped_recipient_who_blocks_unrated_items_gets_no_customrating()
    {
        _gw.Users[_recipient] = _gw.Users[_recipient] with { CapScore = null, CapSubScore = null, BlocksUnrated = true, BlockedUnratedKinds = new[] { "Movie" } };

        Assert.Null(Check().CustomRating);
    }

    [Fact]
    public void AlreadySharedBeatsRecipientAlreadySees()
    {
        _store.Upsert(Record(_item, _recipient));
        _gw.SetVisible(_recipient, _item);
        AssertRefused(Check(), Refusal.AlreadyShared);
    }

    [Theory]
    [InlineData(ShareState.Broken)]
    [InlineData(ShareState.Closed)]
    public void NonActiveRecordDoesNotBlock(ShareState state)
    {
        _store.Upsert(Record(_item, _recipient, state));
        Assert.Equal(Refusal.None, Check().Refusal);
    }

    [Fact]
    public void ActiveRecordForAnotherRecipientDoesNotBlock()
    {
        _store.Upsert(Record(_item, Guid.NewGuid()));
        Assert.Equal(Refusal.None, Check().Refusal);
    }

    [Fact]
    public void ResharedCopyWhenSharerCannotSeeOriginal()
    {
        var original = Guid.NewGuid();
        _gw.Items[original] = new ItemInfo(original, ShareKind.Movie, "Spike", 2001, "15", new Dictionary<string, string>(), _lib, new List<string>(), "/media/Spike.mkv");
        var (copy, _) = MakeCopy(original);
        var other = Guid.NewGuid();
        AddUser(other, "Kid2", cap: 18);
        _gw.SetVisible(_recipient, copy);
        var d = Sut().Check(_recipient, copy, other);
        AssertRefused(d, Refusal.ResharedCopy);
    }

    [Fact]
    public void AdultCanShareFromTheCopyAndItActsOnTheOriginal()
    {
        var original = Guid.NewGuid();
        _gw.Items[original] = new ItemInfo(original, ShareKind.Movie, "Spike", 2001, "15", new Dictionary<string, string>(), _lib, new List<string>(), "/media/Spike.mkv");
        var (copy, _) = MakeCopy(original);
        _gw.SetVisible(_sharer, original);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12, sub: 1);

        var d = Sut().Check(_sharer, copy, third);
        Assert.Equal(Refusal.None, d.Refusal);
        Assert.Equal("12", d.CustomRating);
        Assert.Equal(original, d.OriginalItemId);
    }

    [Fact]
    public void AllowedDecisionCarriesTheItemAndRefusalCarriesNone()
    {
        Assert.Equal(_item, Check().OriginalItemId);
        _gw.SetVisible(_recipient, _item);
        Assert.Null(Check().OriginalItemId);
    }

    [Fact]
    public void PluginTagsNeverCauseOrMaskABlockedTagRefusal()
    {
        var swm = LibraryAccessManager.RecipientTag(Guid.NewGuid());
        var other = LibraryAccessManager.RecipientTag(Guid.NewGuid()).ToUpperInvariant();
        AddUser(_recipient, "Kid1", cap: 12, sub: 1, blocked: new[] { swm, other });
        SetTags(swm, other.ToLowerInvariant());
        Assert.Equal(Refusal.None, Check().Refusal);

        AddUser(_recipient, "Kid1", cap: 12, sub: 1, blocked: new[] { swm, "never" });
        SetTags(swm, "never");
        AssertRefused(Check(), Refusal.BlockedTag);
        Assert.DoesNotContain("never", Check().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sharers_own_playlist_tag_blocked_by_the_recipient_never_causes_a_blocked_tag_refusal()
    {
        // Ruling B2: every other user blocks swm-<sharer> once the sharer owns a "Share with" playlist.
        var own = LibraryAccessManager.RecipientTag(_sharer);
        AddUser(_recipient, "Kid1", cap: 12, sub: 1, blocked: new[] { own });
        Assert.Equal(Refusal.None, Check().Refusal);
        SetTags(own); // even an item that carried it
        Assert.Equal(Refusal.None, Check().Refusal);
    }

    [Fact]
    public void ReshareFromCopyChecksVisibilityOfOriginalForRecipientToo()
    {
        var original = Guid.NewGuid();
        _gw.Items[original] = new ItemInfo(original, ShareKind.Movie, "Spike", 2001, "15", new Dictionary<string, string>(), _lib, new List<string>(), "/media/Spike.mkv");
        var (copy, _) = MakeCopy(original);
        _gw.SetVisible(_sharer, original);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12);
        _gw.SetVisible(third, original);
        AssertRefused(Sut().Check(_sharer, copy, third), Refusal.RecipientAlreadySees);
    }

    [Fact]
    public void CopyAlreadySharedWithSameRecipientIsAlreadyShared()
    {
        var original = Guid.NewGuid();
        _gw.Items[original] = new ItemInfo(original, ShareKind.Movie, "Spike", 2001, "15", new Dictionary<string, string>(), _lib, new List<string>(), "/media/Spike.mkv");
        var (copy, _) = MakeCopy(original);
        _gw.SetVisible(_sharer, original);
        AssertRefused(Sut().Check(_sharer, copy, _recipient), Refusal.AlreadyShared);
    }

    [Fact]
    public void ShareLibraryItemWithNoMatchingRecordIsResharedCopy()
    {
        var shareLib = Guid.NewGuid();
        _store.SetLibrary(_recipient, ShareKind.Movie, shareLib);
        var stray = Guid.NewGuid();
        _gw.Items[stray] = new ItemInfo(stray, ShareKind.Movie, "Stray", 2000, "12", new Dictionary<string, string>(), shareLib, new List<string>(), "/shares/Kid1/movies/Stray/Stray.mkv");
        _gw.SetVisible(_sharer, stray);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12);
        AssertRefused(Sut().Check(_sharer, stray, third), Refusal.ResharedCopy);
    }

    [Fact]
    public void FolderNameMustMatchWholePathSegment()
    {
        var original = Guid.NewGuid();
        _gw.Items[original] = new ItemInfo(original, ShareKind.Movie, "Spike", 2001, "15", new Dictionary<string, string>(), _lib, new List<string>(), "/media/Spike.mkv");
        var (copy, _) = MakeCopy(original, folder: "Spike");
        _gw.Items[copy] = _gw.Items[copy] with { Path = "/shares/Kid1/movies/Spiked/Spiked.mkv" };
        _gw.SetVisible(_sharer, original);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12);
        AssertRefused(Sut().Check(_sharer, copy, third), Refusal.ResharedCopy);
    }

    [Fact]
    public void OriginalGoneBehindACopyIsRefused()
    {
        var original = Guid.NewGuid();
        var (copy, _) = MakeCopy(original);
        _gw.SetVisible(_sharer, original);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12);
        AssertRefused(Sut().Check(_sharer, copy, third), Refusal.ResharedCopy);
    }

    private Guid MakeOriginal(string[]? tags = null)
    {
        var original = Guid.NewGuid();
        _gw.Items[original] = new ItemInfo(original, ShareKind.Movie, "Spike", 2001, "15", new Dictionary<string, string>(), _lib, tags ?? Array.Empty<string>(), "/media/Spike.mkv");
        return original;
    }

    [Fact]
    public void CopyResolvesToItsOwnRecipientsRecordNotAnEarlierOneWithTheSameFolderName()
    {
        var originalB = MakeOriginal();
        var originalA = MakeOriginal();
        var recipientB = Guid.NewGuid();
        var libB = Guid.NewGuid();
        _store.SetLibrary(recipientB, ShareKind.Movie, libB);
        _store.Upsert(Record(originalB, recipientB));
        var (copy, _) = MakeCopy(originalA);
        _gw.SetVisible(_sharer, originalA);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12, sub: 1);

        // B's record comes first and has the same folder name, but its original is not visible to the sharer.
        Assert.Equal(Refusal.None, Sut().Check(_sharer, copy, third).Refusal);
    }

    [Fact]
    public void ClosedRecordBehindACopyStillResolvesAndRequiresVisibilityOfTheOriginal()
    {
        var original = MakeOriginal();
        var (copy, _) = MakeCopy(original, state: ShareState.Closed);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12, sub: 1);

        AssertRefused(Sut().Check(_sharer, copy, third), Refusal.ResharedCopy);
        _gw.SetVisible(_sharer, original);
        Assert.Equal(Refusal.None, Sut().Check(_sharer, copy, third).Refusal);
    }

    [Fact]
    public void RecordOfAnotherKindWithSameFolderNameDoesNotResolve()
    {
        var original = MakeOriginal();
        var (copy, _) = MakeCopy(original);
        _store.Upsert(Record(original, _recipient, folder: "Spike (2001) [abc]") with { Kind = ShareKind.Series });
        _store.Remove(_store.All().First(r => r.Kind == ShareKind.Movie).Id);
        _gw.SetVisible(_sharer, original);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12);
        AssertRefused(Sut().Check(_sharer, copy, third), Refusal.ResharedCopy);
    }

    [Fact]
    public void ItemInUnknownLibraryIsACopyEvenWhenTheStoreLostTheLibrary()
    {
        var lostLib = Guid.NewGuid();
        var child = Guid.NewGuid();
        AddUser(child, "Kid1b", cap: 12);
        var copy = Guid.NewGuid();
        _gw.Items[copy] = new ItemInfo(copy, ShareKind.Movie, "Spike", 2001, "12", new Dictionary<string, string>(), lostLib, new List<string>(), "/shares/Kid1/movies/Spike (2001) [abc]/Spike.mkv");
        _gw.SetVisible(child, copy);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12);
        AssertRefused(Sut().Check(child, copy, third), Refusal.ResharedCopy);
    }

    [Fact]
    public void OriginalTagDrivesBlockedTagWhenSharingFromACopy()
    {
        var original = MakeOriginal(new[] { "gore" });
        var (copy, _) = MakeCopy(original);
        _gw.SetVisible(_sharer, original);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12, blocked: new[] { "GORE" });
        var d = Sut().Check(_sharer, copy, third);
        AssertRefused(d, Refusal.BlockedTag);
        Assert.Equal("Kid2's settings block Spike.", d.Message);
    }

    [Fact]
    public void CopyTagIsIgnoredWhenOriginalIsClean()
    {
        var original = MakeOriginal();
        var (copy, _) = MakeCopy(original);
        _gw.Items[copy] = _gw.Items[copy] with { Tags = new[] { "gore" } };
        _gw.SetVisible(_sharer, original);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12, sub: 1, blocked: new[] { "gore" });
        var d = Sut().Check(_sharer, copy, third);
        Assert.Equal(Refusal.None, d.Refusal);
        Assert.Equal("12", d.CustomRating);
        Assert.Equal(original, d.OriginalItemId);
    }

    [Fact]
    public void SharingFromACopyAsksAboutTheOriginalForTheRecipientNeverTheCopy()
    {
        var original = MakeOriginal();
        var (copy, _) = MakeCopy(original);
        _gw.SetVisible(_sharer, original);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 12, sub: 1);
        Assert.Equal(Refusal.None, Sut().Check(_sharer, copy, third).Refusal);

        var canSee = _gw.Calls.Where(c => c.Method == nameof(IServerGateway.CanSee)).ToList();
        Assert.Contains(canSee, c => (Guid)c.Args[0]! == third && (Guid)c.Args[1]! == original);
        Assert.DoesNotContain(canSee, c => (Guid)c.Args[0]! == third && (Guid)c.Args[1]! == copy);
    }

    [Fact]
    public void GatewayExceptionPropagates()
    {
        _gw.ThrowOnce = nameof(IServerGateway.CanSee);
        Assert.Throws<InvalidOperationException>(() => Check());
    }

    [Fact]
    public void GetRatingsExceptionPropagates()
    {
        _gw.ThrowOnce = nameof(IServerGateway.GetRatings);
        Assert.Throws<InvalidOperationException>(() => Check());
    }

    [Fact]
    public void UnknownInvisibleAndInvisibleOtherTypeAllGetOneFixedAnswer()
    {
        var invisibleMovie = Guid.NewGuid();
        _gw.Items[invisibleMovie] = new ItemInfo(invisibleMovie, ShareKind.Movie, "Secret Film", 2001, "18", new Dictionary<string, string>(), _lib, new List<string>(), "/media/secret.mkv");
        var invisibleEpisode = Guid.NewGuid();
        _gw.Items[invisibleEpisode] = new ItemInfo(invisibleEpisode, null, "Secret Episode", 2001, "18", new Dictionary<string, string>(), _lib, new List<string>(), "/media/secret-ep.mkv");

        var answers = new[] { Guid.NewGuid(), invisibleMovie, invisibleEpisode }
            .Select(id => Sut().Check(_sharer, id, _recipient))
            .ToList();

        Assert.All(answers, d =>
        {
            AssertRefused(d, Refusal.NotVisibleToSharer);
            Assert.Equal("You can't share that title.", d.Message);
            Assert.Null(d.OriginalItemId);
        });
    }

    [Fact]
    public void VisibleNonShareableTypeMayNameItself()
    {
        var episode = Guid.NewGuid();
        _gw.Items[episode] = new ItemInfo(episode, null, "Pilot", 2001, "PG", new Dictionary<string, string>(), _lib, new List<string>(), "/media/pilot.mkv");
        _gw.SetVisible(_sharer, episode);
        AssertRefused(Sut().Check(_sharer, episode, _recipient), Refusal.NotShareableType);
    }

    [Fact]
    public void ASeriesIsShareableButItsSeasonsAndEpisodesAreNot()
    {
        _gw.Items[_item] = _gw.Items[_item] with { Kind = ShareKind.Series, Title = "Spike Show" };
        Assert.Equal(Refusal.None, Check().Refusal);

        // The gateway maps a Season or an Episode to Kind null; visible ones may name themselves.
        foreach (var name in new[] { "Series 1", "Pilot" })
        {
            var id = Guid.NewGuid();
            _gw.Items[id] = new ItemInfo(id, null, name, 2010, null, new Dictionary<string, string>(), _lib, new List<string>(), "/media/show/" + name);
            _gw.SetVisible(_sharer, id);
            AssertRefused(Sut().Check(_sharer, id, _recipient), Refusal.NotShareableType);

            // ...and an invisible one gets the fixed answer, before anything about its type.
            _gw.SetVisible(_sharer, id, false);
            var d = Sut().Check(_sharer, id, _recipient);
            AssertRefused(d, Refusal.NotVisibleToSharer);
            Assert.Equal(ShareEligibility.CannotShareMessage, d.Message);
        }
    }

    [Fact]
    public void ResharedCopyMessageNamesTheCopyNeverTheOriginal()
    {
        var original = Guid.NewGuid();
        _gw.Items[original] = new ItemInfo(original, ShareKind.Movie, "Real Original Name", 2001, "15", new Dictionary<string, string>(), _lib, new List<string>(), "/media/o.mkv");
        var (copy, _) = MakeCopy(original);
        _gw.Items[copy] = _gw.Items[copy] with { Title = "Copy Shown Name" };
        _gw.SetVisible(_recipient, copy);
        var third = Guid.NewGuid();
        AddUser(third, "Kid2", cap: 18);

        var d = Sut().Check(_recipient, copy, third);

        AssertRefused(d, Refusal.ResharedCopy);
        Assert.Equal("Copy Shown Name was shared with you, so you can't pass it on.", d.Message);
        Assert.DoesNotContain("Original", d.Message, StringComparison.Ordinal);
    }
}
