using GpuMonitor.Core.Diagnostics;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class RedactTests
{
    private static Redactor Jeff() => new("Jeff", "DESKTOP-7", @"C:\Users\Jeff");

    [Fact]
    public void HomePath_Replaced() =>
        Assert.Equal(@"<home>\AppData", Jeff().Apply(@"c:\users\JEFF\AppData"));

    [Fact]
    public void HomePath_JsonEscaped_Replaced() =>
        Assert.Equal(@"<home>\\AppData", Jeff().Apply(@"C:\\Users\\Jeff\\AppData"));

    [Fact]
    public void HomePath_ForwardSlash_Replaced() =>
        Assert.Equal("<home>/.config", new Redactor("al", "box", "/home/al").Apply("/home/al/.config"));

    [Fact]
    public void UserAndHost_Replaced() =>
        Assert.Equal("<user> on <host>; Jeffrey stays", Jeff().Apply("Jeff on desktop-7; Jeffrey stays"));

    [Fact]
    public void ShortUserName_DoesNotEatWords()
    {
        var r = new Redactor("al", "PC", @"C:\Users\al");
        var text = @"total also al. in prose, path C:\Users\al\x and D:\data\al\y";
        Assert.Equal(@"total also al. in prose, path <home>\x and D:\data\<user>\y", r.Apply(text));
    }

    [Fact]
    public void NonAsciiUserName()
    {
        var r = new Redactor("Jöns", "PC", @"C:\Users\Jöns");
        Assert.Equal(@"<home>\x and <user> said hi; Jönsson stays", r.Apply(@"C:\Users\Jöns\x and Jöns said hi; Jönsson stays"));
    }
}
