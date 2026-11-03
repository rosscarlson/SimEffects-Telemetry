using GtTelemetry.Core.Updates;
using Xunit;

public class UpdateTests
{
    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("0.3", "0.3.0")]
    [InlineData("V1.2.3-beta", "1.2.3")]
    [InlineData("v2", "2.0.0")]
    public void ParsesReleaseTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void IgnoresTagsThatArentVersions(string? tag) => Assert.Null(UpdateChecker.ParseVersion(tag));

    [Fact]
    public void AvailableNeedsANewerReleaseWithAnInstaller()
    {
        var current = new Version(0, 2, 0);
        Release R(string v, string? url) => new(Version.Parse(v), "v" + v, url, "Setup.exe", "", "");
        Assert.True(new UpdateInfo(current, R("0.3.0", "https://x/Setup.exe"), 1).Available);
        Assert.False(new UpdateInfo(current, R("0.3.0", null), 1).Available);
        Assert.False(new UpdateInfo(current, R("0.2.0", "https://x/Setup.exe"), 0).Available);
    }
}
