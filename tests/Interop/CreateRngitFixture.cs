// dotnet run --file tests\Interop\CreateRngitFixture.cs -- <owned-root>
// Add "incremental <clone>" or "push <clone>" to prepare independently owned histories.
#:property LangVersion=13.0
#:property Nullable=enable
#:property ImplicitUsings=enable

using System.Diagnostics;
using System.Text;
using System.Text.Json;

const string UnicodeBranch = "branch-caf\u00e9";
const string UnicodeTag = "v-\u00e9";
Console.OutputEncoding = new UTF8Encoding(false, true);

if (args.Length != 1 && (args.Length != 3 || args[1] is not ("incremental" or "push")))
{
    Console.Error.WriteLine("Usage: CreateRngitFixture.cs <owned-root> [incremental|push <clone>]");
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
try
{
    var root = Path.GetFullPath(args[0]);
    if (!Directory.Exists(root))
        throw new DirectoryNotFoundException("The caller must create its owned root first.");
    var source = Path.Combine(root, "source");
    var repositories = Path.Combine(root, "repositories");
    var bare = Path.Combine(repositories, "fixture.git");
    if (args.Length == 1)
    {
        if (Directory.Exists(source) || Directory.Exists(repositories))
            throw new IOException("Refusing to overwrite an existing fixture.");
        Directory.CreateDirectory(repositories);
        await Git("init", "--initial-branch=main", "--object-format=sha1", source);
        await Write(Path.Combine(source, "readme.txt"), "independent main fixture\n");
        await Git("-C", source, "add", "readme.txt");
        await Git("-C", source, "commit", "-m", "initial main");
        var initialCommit = await Git("-C", source, "rev-parse", "HEAD");
        var initialTree = await Git("-C", source, "rev-parse", "HEAD^{tree}");
        await Git("-C", source, "tag", "-a", "v1", "-m", "annotated version one");
        var annotatedTag = await Git("-C", source, "rev-parse", "refs/tags/v1");
        await Git("-C", source, "branch", UnicodeBranch, initialCommit);
        await Git("-C", source, "tag", UnicodeTag, initialCommit);
        await Git("-C", source, "switch", "-c", "feature");
        await Write(Path.Combine(source, "feature.txt"), "nondefault branch fixture\n");
        await Git("-C", source, "add", "feature.txt");
        await Git("-C", source, "commit", "-m", "feature branch");
        var featureCommit = await Git("-C", source, "rev-parse", "HEAD");
        await Git("-C", source, "tag", "feature-tip");
        await Git("-C", source, "switch", "main");
        await Git("clone", "--bare", source, bare);
        var pushBare = Path.Combine(repositories, "push.git");
        await Git("clone", "--bare", source, pushBare);
        await Git("-C", pushBare, "tag", "locked", initialCommit);
        var emptyBare = Path.Combine(repositories, "empty.git");
        await Git("init", "--bare", "--initial-branch=main", "--object-format=sha1", emptyBare);
        var sha256Source = Path.Combine(root, "sha256-source");
        var sha256Bare = Path.Combine(repositories, "sha256.git");
        await Git("init", "--initial-branch=main", "--object-format=sha256", sha256Source);
        await Write(Path.Combine(sha256Source, "readme.txt"), "actual SHA256 fixture\n");
        await Git("-C", sha256Source, "add", "readme.txt");
        await Git("-C", sha256Source, "commit", "-m", "SHA256 initial");
        await Git("clone", "--bare", sha256Source, sha256Bare);
        Print(("Source", source), ("Repositories", repositories), ("BareSource", bare),
            ("InitialCommit", initialCommit), ("InitialTree", initialTree),
            ("AnnotatedTag", annotatedTag), ("FeatureCommit", featureCommit),
            ("UnicodeBranch", UnicodeBranch), ("UnicodeTag", UnicodeTag),
            ("PushBare", pushBare), ("EmptyBare", emptyBare),
            ("Sha256Source", sha256Source), ("Sha256Bare", sha256Bare));
    }
    else if (args[1] == "incremental")
    {
        var clone = Path.GetFullPath(args[2]);
        var relative = Path.GetRelativePath(root, clone);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            !Directory.Exists(Path.Combine(clone, ".git")) || !Directory.Exists(bare))
            throw new IOException("Incremental mode requires a working clone inside the owned fixture root.");
        await Write(Path.Combine(clone, "local.txt"), "local branch must survive fetch\n");
        await Git("-C", clone, "add", "local.txt");
        await Git("-C", clone, "commit", "-m", "local only");
        var localCommit = await Git("-C", clone, "rev-parse", "HEAD");
        await Write(Path.Combine(source, "next.txt"), $"new server object {Guid.NewGuid()}\n");
        await Git("-C", source, "add", "next.txt");
        await Git("-C", source, "commit", "-m", "incremental main");
        var nextCommit = await Git("-C", source, "rev-parse", "HEAD");
        var nextTree = await Git("-C", source, "rev-parse", "HEAD^{tree}");
        await Git("-C", source, "tag", "v2");
        await Git("-C", source, "branch", "-f", UnicodeBranch, nextCommit);
        await Git("-C", source, "push", bare, "main", $"refs/heads/{UnicodeBranch}", "refs/tags/v2");
        Print(("LocalCommit", localCommit), ("NextCommit", nextCommit), ("NextTree", nextTree));
    }
    else
    {
        var clone = Path.GetFullPath(args[2]);
        var relative = Path.GetRelativePath(root, clone);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Directory.Exists(clone))
            throw new IOException("Push mode requires a new working clone inside the owned fixture root.");
        await Git("clone", source, clone);
        var baseCommit = await Git("-C", clone, "rev-parse", "HEAD");
        await Write(Path.Combine(clone, "push.txt"), "new independently generated push object\n");
        await Git("-C", clone, "add", "push.txt");
        await Git("-C", clone, "commit", "-m", "push first");
        var first = await Git("-C", clone, "rev-parse", "HEAD");
        await Git("-C", clone, "tag", "push-light");
        await Git("-C", clone, "tag", "-a", "push-annotated", "-m", "push annotated tag");
        await Git("-C", clone, "tag", "push-\u00e9");
        var pushAnnotated = await Git("-C", clone, "rev-parse", "refs/tags/push-annotated");
        await Write(Path.Combine(clone, "push.txt"), "second independently generated push object\n");
        await Git("-C", clone, "commit", "-am", "push second");
        var second = await Git("-C", clone, "rev-parse", "HEAD");
        await Git("-C", clone, "switch", "-c", "divergent", baseCommit);
        await Write(Path.Combine(clone, "divergent.txt"), "divergent push history\n");
        await Git("-C", clone, "add", "divergent.txt");
        await Git("-C", clone, "commit", "-m", "push divergence");
        var divergent = await Git("-C", clone, "rev-parse", "HEAD");
        await Git("-C", clone, "switch", "main");
        Print(("Clone", clone), ("FirstCommit", first), ("SecondCommit", second),
            ("DivergentCommit", divergent), ("AnnotatedTag", pushAnnotated));
    }
    return 0;

    async Task Write(string path, string content) =>
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), timeout.Token);

    async Task<string> Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in new[]
        {
            "-c", "safe.bareRepository=all", "-c", "core.autocrlf=false",
            "-c", "user.name=DotReticulum fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false"
        }.Concat(arguments))
            info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new IOException("Could not start Git.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0)
                throw new IOException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr}{stdout}");
            return stdout.Trim();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception is OperationCanceledException
        ? "Git fixture generation timed out after 60 seconds."
        : exception.Message);
    return 1;
}

static void Print(params (string Name, string Value)[] fields)
{
    using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
    writer.WriteStartObject();
    foreach (var (name, value) in fields)
        writer.WriteString(name, value);
    writer.WriteEndObject();
    writer.Flush();
    Console.WriteLine();
}
