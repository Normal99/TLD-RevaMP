using Xunit;

namespace TLDInstalls.Tests;

/// <summary>An automated test copy must be launched through tools/game.py, which owns the dedicated game
/// directory, the dedicated Proton prefix, the explicit bridge port and the launch-time compositor
/// rules.  A direct Copies launch would bypass all of that, so it is refused.</summary>
public sealed class AutomatedLaunchGuardTests : IDisposable
{
    readonly string[] keys = { "TLD_TEST_COMPAT", "TLD_REMOTE_TEST_COMPAT", "TLD_BRIDGE_PORT", "TLD_REMOTE_BRIDGE_PORT" };

    public void Dispose()
    {
        foreach (var k in keys) Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public void OrdinaryLaunchIsNotRefused()
    {
        Launcher.RefuseAutomatedTestLaunch(null);
        Launcher.RefuseAutomatedTestLaunch([]);
        Launcher.RefuseAutomatedTestLaunch(["-screen-fullscreen", "0"]);
    }

    [Theory]
    [InlineData("TLD_TEST_COMPAT")]
    [InlineData("TLD_REMOTE_TEST_COMPAT")]
    [InlineData("TLD_BRIDGE_PORT")]
    [InlineData("TLD_REMOTE_BRIDGE_PORT")]
    public void AnAutomatedTestEnvironmentRefusesTheDirectLaunch(string key)
    {
        // a synthetic path, not a home folder: the privacy scan keeps the machine owner's real layout out of
        // the repository, and this file is tracked
        Environment.SetEnvironmentVariable(key, "/opt/tld-test/run-A/compat");
        var error = Assert.Throws<UserError>(() => Launcher.RefuseAutomatedTestLaunch(null));
        Assert.Contains("tools/game.py", error.Message);
    }

    [Fact]
    public void AnEmptyEnvironmentValueIsNotAnAutomatedTest()
    {
        foreach (var k in keys) Environment.SetEnvironmentVariable(k, "   ");
        Launcher.RefuseAutomatedTestLaunch(null);
    }

    [Fact]
    public void ABridgeArgumentRefusesTheDirectLaunch()
    {
        var error = Assert.Throws<UserError>(() => Launcher.RefuseAutomatedTestLaunch(["-tldbridge", "28101"]));
        Assert.Contains("tools/game.py", error.Message);
    }

    [Fact]
    public void TheBridgeArgumentIsMatchedRegardlessOfCase()
    {
        Assert.Throws<UserError>(() => Launcher.RefuseAutomatedTestLaunch(["-TLDBridge", "28101"]));
    }
}
