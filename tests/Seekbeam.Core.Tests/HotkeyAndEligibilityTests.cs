using Seekbeam.Core.Config;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.Input;

namespace Seekbeam.Core.Tests;

/// <summary>Hotkey capture helpers (format, validation) and the reasons reported for files whose content is not indexed.</summary>
public sealed class HotkeyAndEligibilityTests
{
    [Theory]
    [InlineData(HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_ALT, 'F', "Ctrl+Alt+F")]
    [InlineData(HotkeyParser.MOD_ALT, 0x20, "Alt+Space")]
    [InlineData(HotkeyParser.MOD_WIN | HotkeyParser.MOD_SHIFT, 0x71, "Shift+Win+F2")]
    [InlineData(HotkeyParser.MOD_CONTROL, 0x63, "Ctrl+Num3")]
    [InlineData(HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_SHIFT, 0x28, "Ctrl+Shift+Down")]
    [InlineData(HotkeyParser.MOD_CONTROL, 0xC0, "Ctrl+OEM3")]
    public void FormatRoundTripsThroughParse(uint mods, uint vk, string expected)
    {
        string text = HotkeyParser.Format(mods, vk);
        Assert.Equal(expected, text);
        Assert.True(HotkeyParser.TryParse(text, out uint m, out uint k));
        Assert.Equal(mods, m);
        Assert.Equal(vk, k);
    }

    [Theory]
    [InlineData(HotkeyParser.MOD_CONTROL, 'K', HotkeyValidation.Ok)]
    [InlineData(HotkeyParser.MOD_SHIFT, 0x70, HotkeyValidation.Ok)]               // Shift+F1: not a typing key
    [InlineData(HotkeyParser.MOD_SHIFT, 'A', HotkeyValidation.ShiftOnly)]         // would steal capital letters
    [InlineData(HotkeyParser.MOD_SHIFT, 0x20, HotkeyValidation.ShiftOnly)]
    [InlineData(0u, 'A', HotkeyValidation.NoModifier)]
    [InlineData(HotkeyParser.MOD_CONTROL, 0x5D, HotkeyValidation.UnsupportedKey)] // Apps key
    public void ValidationRejectsUnsafeCombinations(uint mods, uint vk, HotkeyValidation expected) =>
        Assert.Equal(expected, HotkeyParser.Validate(mods, vk));

    private static RootScope Root(bool content = true, bool links = false) => new()
    {
        RootId = 1, Path = @"C:\Docs", Excludes = new ExclusionMatcher([]),
        Config = new RootConfig { Path = @"C:\Docs", IndexContent = content, FollowLinks = links },
    };

    [Fact]
    public void IneligibilityReasons()
    {
        var cfg = new ContentConfig { MaxFileSizeMB = 1, ExcludedExtensions = ["log"], ExcludedFolders = [@"C:\Docs\Downloads"] };
        var e = new ContentEligibility(cfg, ["txt", "pdf", "log"]);
        var root = Root();
        Assert.Equal(ContentIneligibility.None, e.Check(root, @"C:\Docs\a.txt", 10, FileAttributes.Normal));
        Assert.Equal(ContentIneligibility.UnsupportedType, e.Check(root, @"C:\Docs\a.jpg", 10, FileAttributes.Normal));
        Assert.Equal(ContentIneligibility.ExcludedType, e.Check(root, @"C:\Docs\a.log", 10, FileAttributes.Normal));
        Assert.Equal(ContentIneligibility.EmptyFile, e.Check(root, @"C:\Docs\a.txt", 0, FileAttributes.Normal));
        Assert.Equal(ContentIneligibility.TooLarge, e.Check(root, @"C:\Docs\a.txt", 2 * 1024 * 1024, FileAttributes.Normal));
        Assert.Equal(ContentIneligibility.CloudPlaceholder, e.Check(root, @"C:\Docs\a.txt", 10, FileAttributes.Offline));
        Assert.Equal(ContentIneligibility.Link, e.Check(root, @"C:\Docs\a.txt", 10, FileAttributes.ReparsePoint));
        Assert.Equal(ContentIneligibility.ExcludedFolder, e.Check(root, @"C:\Docs\Downloads\a.txt", 10, FileAttributes.Normal));
        Assert.Equal(ContentIneligibility.RootDisabled, e.Check(Root(content: false), @"C:\Docs\a.txt", 10, FileAttributes.Normal));
        Assert.Equal(ContentIneligibility.Disabled, new ContentEligibility(new ContentConfig { Enabled = false }, ["txt"]).Check(root, @"C:\Docs\a.txt", 10, FileAttributes.Normal));
        // IsEligible stays the same predicate.
        Assert.True(e.IsEligible(root, @"C:\Docs\a.txt", 10, FileAttributes.Normal));
        Assert.False(e.IsEligible(root, @"C:\Docs\a.jpg", 10, FileAttributes.Normal));
    }
}
