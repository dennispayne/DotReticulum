using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rngit;

namespace DotReticulum.Applications.Tests;

[Trait("Category", "Unit")]
public sealed class RngitTemplatesTests : IDisposable
{
    // Actual upstream a29bfb51c468945c96a1ee22cae0114e993e2391 execution.
    // GenerateRngitMediaTemplatesVectors.py records source hashes and discovery shims.
    private readonly PresentationProcessFixture fixture = new();
    private readonly JsonDocument vectors = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "rngit-media-templates.json")));

    [Fact]
    public async Task StaticUnicodeLoadingSubstitutionsAndDefaultPageMatchUpstream()
    {
        var reference = vectors.RootElement;
        File.WriteAllText(Path.Combine(fixture.Directory, "vector.mu"), reference.GetProperty("template_input").GetString(), new UTF8Encoding(false));
        var templates = new RngitTemplates(fixture.Directory);
        Assert.Equal(reference.GetProperty("template_loaded").GetString(), await templates.LoadAsync("vector"));
        Assert.Equal("  π content {UNKNOWN}", await templates.RenderAsync("vector",
            new Dictionary<string, string> { ["PAGE_CONTENT"] = "content" }));
        var rendered = await templates.RenderPageAsync("a\t`b\n", "Nœud ♥", "1.5.7", "Navigation ♥", "front");
        Assert.Equal(reference.GetProperty("rendered").GetString(), Encoding.UTF8.GetString(rendered));
        Assert.Null(await templates.LoadAsync("absent"));
    }

    [Fact]
    public async Task CustomBaseAndEmptyTemplateFallbackPreserveSequentialReplacement()
    {
        File.WriteAllText(Path.Combine(fixture.Directory, "base.mu"), "{NODE_NAME}|{VERSION}|{NAVIGATION}|{GEN_TIME}|{PAGE_CONTENT}\n");
        File.WriteAllText(Path.Combine(fixture.Directory, "front.mu"), "\n");
        var templates = new RngitTemplates(fixture.Directory);
        Assert.Equal("N|v||Unknown generation time|> Groups\n\n  x",
            Encoding.UTF8.GetString(await templates.RenderPageAsync("  x", "N", "v", template: "front")));
        File.WriteAllText(Path.Combine(fixture.Directory, "front.mu"), "Custom: {PAGE_CONTENT}");
        Assert.Equal("N|v||Unknown generation time|Custom: x",
            Encoding.UTF8.GetString(await templates.RenderPageAsync("x", "N", "v", template: "front")));
    }

    [Fact]
    public void UnicodeAndNerdFontIconsAndFiniteMarkupMatchUpstream()
    {
        foreach (var mode in vectors.RootElement.GetProperty("icons").EnumerateObject())
            foreach (var icon in mode.Value.EnumerateObject())
                Assert.Equal(icon.Value.GetString(), RngitTemplates.Icon(icon.Name, mode.Name == "true"));
        var markup = vectors.RootElement.GetProperty("markup");
        Assert.Equal(markup.GetProperty("escape").GetString(), RngitTemplates.Escape("`x` [unchanged]"));
        Assert.Equal(markup.GetProperty("tabs").GetString(), RngitTemplates.FormatTabs("a\tb\tc"));
        Assert.Equal(markup.GetProperty("diff").GetString(), RngitTemplates.FormatDiff(markup.GetProperty("diff_input").GetString()!));
        Assert.Equal(markup.GetProperty("link").GetString(), RngitTemplates.Link("[`Nœud]", "/page/tree.mu",
            new Dictionary<string, string> { ["g"] = "hello world", ["r"] = "a+b/π" }));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("..")]
    public async Task TraversalNamesAreRejected(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new RngitTemplates(fixture.Directory).LoadAsync(name));
    }

    [Fact]
    public async Task InvalidUtf8AndOversizedInputAreDiagnosed()
    {
        var diagnostics = new List<string>();
        var templates = new RngitTemplates(fixture.Directory, maxOutputBytes: 64, diagnostic: diagnostics.Add);
        File.WriteAllBytes(Path.Combine(fixture.Directory, "invalid.mu"), [0xff]);
        Assert.Null(await templates.LoadAsync("invalid"));
        File.WriteAllText(Path.Combine(fixture.Directory, "large.mu"), new string('x', 65));
        Assert.Null(await templates.LoadAsync("large"));
        Assert.Equal(2, diagnostics.Count);
        File.WriteAllText(Path.Combine(fixture.Directory, "repeat.mu"), "{X}{X}{X}");
        await Assert.ThrowsAsync<IOException>(() => templates.RenderAsync("repeat",
            new Dictionary<string, string> { ["X"] = new string('π', 32) }));
    }

    [Fact]
    public async Task ExecutableTemplatesRequireExplicitAuthorizationAndFailuresAreDiagnosed()
    {
        var path = Path.Combine(fixture.Directory, "dynamic.mu");
        File.Copy(fixture.Backend, path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        fixture.BackendMode("fail");
        var diagnostics = new List<string>();
        Assert.Null(await new RngitTemplates(fixture.Directory, diagnostic: diagnostics.Add).LoadAsync("dynamic"));
        Assert.Contains("denied", diagnostics.Single());
        diagnostics.Clear();
        Assert.Null(await new RngitTemplates(fixture.Directory, allowExecutable: true, diagnostic: diagnostics.Add).LoadAsync("dynamic"));
        Assert.Contains("failed", diagnostics.Single());
        diagnostics.Clear();
        fixture.BackendMode("template");
        Assert.Equal("owned π {PAGE_CONTENT}", await new RngitTemplates(fixture.Directory, allowExecutable: true, diagnostic: diagnostics.Add).LoadAsync("dynamic"));
        Assert.Empty(diagnostics);
        fixture.BackendMode("timeout");
        Assert.Null(await new RngitTemplates(fixture.Directory, allowExecutable: true, timeout: TimeSpan.FromMilliseconds(500),
            diagnostic: diagnostics.Add).LoadAsync("dynamic"));
        Assert.Contains("timed out", diagnostics.Single());
        diagnostics.Clear();
        fixture.BackendMode("overflow");
        Assert.Null(await new RngitTemplates(fixture.Directory, allowExecutable: true, maxOutputBytes: 256,
            diagnostic: diagnostics.Add).LoadAsync("dynamic"));
        Assert.Contains("byte combined stdout/stderr limit", diagnostics.Single());
    }

    [Fact]
    public async Task EditorAuthorizationSuccessFailureTimeoutAndCleanup()
    {
        var diagnostics = new List<string>();
        var templates = new RngitTemplates(fixture.Directory, timeout: TimeSpan.FromSeconds(15), diagnostic: diagnostics.Add);
        Assert.Null(await templates.EditAsync("original", fixture.Editor("editor", authorized: false)));
        Assert.Contains("denied", diagnostics.Single());
        Assert.Equal("edited π\n", await templates.EditAsync("original", fixture.Editor("editor")));
        Assert.Null(await templates.EditAsync("original", fixture.Editor("editor-fail")));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, ".rngit-edit-*"));
        var limited = new RngitTemplates(fixture.Directory, timeout: TimeSpan.FromSeconds(15), maxOutputBytes: 512, diagnostic: diagnostics.Add);
        Assert.Null(await limited.EditAsync("original", fixture.Editor("editor-large")));
        var timed = new RngitTemplates(fixture.Directory, timeout: TimeSpan.FromMilliseconds(500), diagnostic: diagnostics.Add);
        Assert.Null(await timed.EditAsync("original", fixture.Editor("sleep")));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => templates.EditAsync("original", fixture.Editor("sleep"), stop.Token));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, ".rngit-edit-*"));
    }

    [Fact]
    public void EditorParsingPreservesLiteralArgumentsAndWindowsPathsWithoutShellExpansion()
    {
        var editor = RngitTemplates.ParseEditor("\"C:\\Program Files\\editor.exe\" --wait 'π value' '$HOME;&|'", authorized: true);
        Assert.Equal("C:\\Program Files\\editor.exe", editor.File);
        Assert.Equal(new[] { "--wait", "π value", "$HOME;&|" }, editor.Arguments);
        Assert.True(editor.Authorized);
        Assert.False(RngitTemplates.ParseEditor("vi").Authorized);
        Assert.Throws<ArgumentException>(() => RngitTemplates.ParseEditor("vi 'bad"));
    }

    [Fact]
    public async Task PrecancelledLoadingAndEditingDoNotCreateFiles()
    {
        var templates = new RngitTemplates(fixture.Directory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => templates.LoadAsync("absent", new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => templates.EditAsync("input", fixture.Editor("editor"), new CancellationToken(true)));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, ".rngit-edit-*"));
    }

    public void Dispose() { vectors.Dispose(); fixture.Dispose(); }
}
