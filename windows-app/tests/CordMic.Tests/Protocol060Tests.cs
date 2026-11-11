using CordMic.Core;
using Xunit;

namespace CordMic.Tests;

/// <summary>0.6.0 wire additions: diag flag bits 3–5 and the Studio PING flag. Same golden bytes as the Kotlin test.</summary>
public class Protocol060Tests
{
    static readonly byte[] Golden060 = { 0xC7, 0xC2, 0x11, 0x3F, 0xF4, 0x85 };

    [Fact]
    public void DiagFlagBitsMatchKotlin()
    {
        var d = new PhoneDiag(-62, true, true, 8.5, -6, true, Silenced: true, StudioBypass: true, NsSuspected: true);
        var buf = new byte[PhoneDiag.Size];
        d.Write(buf);
        Assert.Equal(Golden060, buf);
        Assert.True(PhoneDiag.TryRead(buf, out var back));
        Assert.Equal(d, back);
    }

    [Fact]
    public void OldDiagReadsWithNewFlagsOff()
    {
        Assert.True(PhoneDiag.TryRead(new byte[] { 0xC7, 0xC2, 0x11, 0x07, 0xF4, 0xBD }, out var d));
        Assert.False(d.Silenced); Assert.False(d.StudioBypass); Assert.False(d.NsSuspected);
    }

    [Fact]
    public void StudioFlagIsBit5AndDoesNotOverlap()
    {
        Assert.Equal(32, SettingsSync.FlagStudio);
        Assert.Equal(0, SettingsSync.FlagStudio & SettingsSync.FlagWantDiag);
    }
}
