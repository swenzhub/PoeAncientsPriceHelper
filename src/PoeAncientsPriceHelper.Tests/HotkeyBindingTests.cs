using PoeAncientsPriceHelper;
using SharpHook.Data;

namespace PoeAncientsPriceHelper.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("VcF7", KeyCode.VcF7)]
    [InlineData("VcA", KeyCode.VcA)]
    [InlineData("VcF5", KeyCode.VcF5)]
    public void Parse_ValidName_ReturnsKey(string stored, KeyCode expected)
    {
        Assert.Equal(expected, HotkeyBinding.Parse(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("12345")]
    public void Parse_InvalidName_FallsBackToDefault(string? stored)
    {
        Assert.Equal(HotkeyBinding.Default, HotkeyBinding.Parse(stored));
    }

    [Theory]
    [InlineData(KeyCode.VcF5)]
    [InlineData(KeyCode.VcP)]
    [InlineData(KeyCode.VcNumPad0)]
    public void StorageRoundTrips(KeyCode key)
    {
        Assert.Equal(key, HotkeyBinding.Parse(HotkeyBinding.ToStorage(key)));
    }

    [Theory]
    [InlineData(KeyCode.VcF5, "F5")]
    [InlineData(KeyCode.VcA, "A")]
    [InlineData(KeyCode.Vc1, "1")]
    public void Display_StripsVcPrefix(KeyCode key, string expected)
    {
        Assert.Equal(expected, HotkeyBinding.Display(key));
    }

    [Theory]
    [InlineData(KeyCode.VcEscape, true)]
    [InlineData(KeyCode.VcLeftControl, true)]
    [InlineData(KeyCode.VcRightControl, true)]
    // F3/F4 are now ordinary rebindable defaults, not reserved gestures.
    [InlineData(KeyCode.VcF3, false)]
    [InlineData(KeyCode.VcF4, false)]
    [InlineData(KeyCode.VcF5, false)]
    [InlineData(KeyCode.VcP, false)]
    public void IsReserved_FlagsOnlyFixedGestures(KeyCode key, bool reserved)
    {
        Assert.Equal(reserved, HotkeyBinding.IsReserved(key));
    }
}
