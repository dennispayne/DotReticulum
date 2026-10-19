using DotReticulum.Applications.Rngit;

namespace DotReticulum.Applications.Tests;

public sealed class GitRemoteRnsTests
{
    private const string Url = "rns://0123456789abcdef0123456789abcdef/group/repo";

    [Fact]
    public async Task Negotiation_IsOfflineReadOnlyAndProtocolClean()
    {
        using var input = new StringReader("capabilities\noption progress true\noption progress false\noption depth 1\noption progress nonsense\n\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await GitRemoteRnsApp.RunAsync(["origin", Url], input, output, error, CancellationToken.None));
        Assert.Equal("fetch\noption\n\nok\nok\nunsupported\nerror invalid progress value\n", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("push refs/heads/main:refs/heads/main\n", "Unsupported")]
    [InlineData("list for-push\n", "Push is not supported")]
    [InlineData("connect git-upload-pack\n", "Unsupported")]
    [InlineData("option progress\n", "Malformed")]
    [InlineData("fetch broken\n", "preceding list")]
    [InlineData("capabilities", "Truncated")]
    [InlineData("\0\n", "Malformed")]
    public async Task MalformedAndUnsupportedCommands_FailExplicitly(string command, string diagnostic)
    {
        using var input = new StringReader(command);
        using var error = new StringWriter();
        Assert.Equal(1, await GitRemoteRnsApp.RunAsync(["origin", Url], input, TextWriter.Null, error, CancellationToken.None));
        Assert.Contains(diagnostic, error.ToString());
    }

    [Fact]
    public async Task OversizedCommand_IsRejected()
    {
        using var input = new StringReader(new string('x', 8193) + "\n");
        using var error = new StringWriter();
        Assert.Equal(1, await GitRemoteRnsApp.RunAsync(["origin", Url], input, TextWriter.Null, error, CancellationToken.None));
        Assert.Contains("oversized", error.ToString());
    }

    [Fact]
    public async Task Cancellation_IsReportedWithoutProtocolOutput()
    {
        using var input = new StreamReader(new MemoryStream());
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(1, await GitRemoteRnsApp.RunAsync(["origin", Url], input, output, error, cancelled.Token));
        Assert.Empty(output.ToString());
        Assert.Contains("Cancelled", error.ToString());
    }

    [Theory]
    [InlineData("refs/heads/main", true)]
    [InlineData("refs/tags/release.v1", true)]
    [InlineData("refs/heads/a/b", true)]
    [InlineData("refs/heads/main\ninjected", false)]
    [InlineData("refs/heads/main.lock", false)]
    [InlineData("refs/heads/.hidden", false)]
    [InlineData("refs/heads/a..b", false)]
    [InlineData("refs/heads/a@{b", false)]
    [InlineData("refs/heads/a\\b", false)]
    [InlineData("refs/heads//b", false)]
    [InlineData("refs/heads/a.", false)]
    public async Task RefNames_ValidateBeforeProtocolEmission(string name, bool valid)
    {
        Assert.Equal(valid, RngitSession.ValidRef(name));
        var check = RngitClient.GitAsync(Directory.GetCurrentDirectory(), ["check-ref-format", name], CancellationToken.None);
        if (valid) await check;
        else await Assert.ThrowsAsync<IOException>(() => check);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("@refs/heads/main HEAD\n@refs/heads/other HEAD\n")]
    [InlineData("abcd refs/heads/main\n@refs/heads/main HEAD\n")]
    [InlineData("@refs/tags/v1 HEAD\n")]
    public void MalformedRemoteListings_AreRejected(string text) =>
        Assert.Throws<InvalidDataException>(() => RngitClient.ParseRefs(text));
}
