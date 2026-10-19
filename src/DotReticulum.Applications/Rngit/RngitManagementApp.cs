using System.Globalization;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public static class RngitManagementApp
{
    public const string Usage = """
        DotReticulum rngit - experimental repository node and management

        Usage:
          rngit [node] [--config <directory>] [--rnsconfig <directory>] [-p] [-s]
          rngit create <rns://node/group/repository> [options]
          rngit fork|mirror <source-url> <target-url> [options]
          rngit sync <repository-url> [options]
          rngit perms <group-or-repository-url> [options]
          rngit work <repository-url> <operation> [--scope active|completed|proposed|all]
          rngit release <repository-url> <operation> [target] [options]
          rngit clone|fetch ...                 Existing bare object-transfer client

        Management options:
          --config <directory>    rngit configuration/identity directory
          --rnsconfig <directory> .NET Reticulum configuration (required to run networking)
          -i, --identity <file>   Raw Reticulum private identity file
          -t, --title <title>     Work document title
          -d, --id <number>       Work document ID
          -s, --signer <file>     Release signing identity
          -n, --name <name>       Release package name
          -L, --local            Prepare release locally
          -o, --offline          Verify release without networking
          --content <file>       Explicit owned content input (non-interactive extension)
          --allow-exec           Explicitly authorize bounded EDITOR/template execution
          --allow-external       Node operator permits HTTP/HTTPS/SSH repository sources
          --yes                  Confirm deletion/latest update without a prompt
          --timeout <seconds>    Overall deadline (default 7200)
          -v, --verbose          Increase diagnostic verbosity
          -q, --quiet            Decrease diagnostic verbosity
          --version              Print pinned compatibility version
          -h, --help             Show help

        Executable discovery: install the native binary as rngit and rngcs as well
        as git-remote-rns. Editor execution is denied unless --allow-exec is given.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        Options options;
        try { options = Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            error.WriteLine("rngit: " + ex.Message);
            return 2;
        }
        var serving = false;
        try
        {
            serving = options.Command == "node";
            if (options.Help) { output.WriteLine(Usage); return 0; }
            if (options.Version) { output.WriteLine("rngit 1.5.7"); return 0; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            if (options.Command != "node") deadline.CancelAfter(options.Timeout);
            if (options.Command == "node")
                return await RunNodeAsync(options, output, error, deadline.Token).ConfigureAwait(false);
            return await RunClientAsync(options, output, error, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            error.WriteLine("rngit: " + ex.Message);
            return serving ? 255 : 1;
        }
        catch (OperationCanceledException)
        {
            if (serving && stop.IsCancellationRequested) return 0;
            error.WriteLine(stop.IsCancellationRequested ? "rngit: Cancelled; remote outcome may be uncertain." : "rngit: Overall timeout expired.");
            return serving ? 255 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine("rngit: " + ex.Message);
            return serving ? 255 : 1;
        }
    }

    private static async Task<int> RunNodeAsync(Options options, TextWriter output, TextWriter error, CancellationToken stop)
    {
        var config = RngitHostConfiguration.Load(options.Config, message => error.WriteLine(message));
        if (options.PrintIdentity)
        {
            using var identity = config.LoadOrCreateIdentity();
            using var clientIdentity = RngitClientConfiguration.Load(config.DirectoryPath).LoadOrCreateIdentity();
            output.WriteLine($"Git Peer Identity         : <{clientIdentity.HexHash}>");
            output.WriteLine($"Repository Node Identity  : <{identity.HexHash}>");
            output.WriteLine($"Repositories Destination  : <{Convert.ToHexStringLower(Destination.Create(DestinationType.Single, "git", ["repositories"], identity).Hash)}>");
            if (config.ServeNomadnet)
                output.WriteLine($"Nomad Network Destination : <{Convert.ToHexStringLower(Destination.Create(DestinationType.Single, "nomadnetwork", ["node"], identity).Hash)}>");
            return 0;
        }
        var logLevel = Math.Clamp(config.LogLevel + options.Verbosity, -1, 8);
        Action<int, string> logger = options.Service
            ? GitRemoteRnsApp.CreateFileLogger(Path.Combine(config.DirectoryPath, "server_log"), logLevel, error, "rngit", "server_log")
            : (level, message) => { if (level <= logLevel) error.WriteLine(message); };
        await using var node = ReticulumNode.Open(options.RnsConfig, logger);
        await using var host = new RngitHost(node, config, message => logger(3, message),
            allowExecutable: options.AllowExec, allowExternalSources: options.AllowExternal);
        await node.StartAsync(stop).ConfigureAwait(false);
        output.WriteLine($"Reticulum Git Node listening on <{host.DestinationHashHex}>");
        await output.FlushAsync(stop).ConfigureAwait(false);
        var nextAnnounce = DateTimeOffset.MinValue;
        var nextSync = DateTimeOffset.UtcNow.AddMinutes(15);
        var nextFlush = DateTimeOffset.UtcNow.AddMinutes(3);
        while (!stop.IsCancellationRequested)
        {
            if (config.AnnounceInterval != TimeSpan.Zero && DateTimeOffset.UtcNow >= nextAnnounce)
            {
                await host.AnnounceAsync(stop).ConfigureAwait(false);
                nextAnnounce = DateTimeOffset.UtcNow + config.AnnounceInterval;
            }
            if (config.MirrorInterval > TimeSpan.Zero && DateTimeOffset.UtcNow >= nextSync)
            {
                await host.Store.SyncMirrorsAsync(stop).ConfigureAwait(false);
                nextSync = DateTimeOffset.UtcNow.AddMinutes(15);
            }
            if (DateTimeOffset.UtcNow >= nextFlush)
            {
                host.Statistics.Flush();
                nextFlush = DateTimeOffset.UtcNow.AddMinutes(3);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> RunClientAsync(Options options, TextWriter output, TextWriter error, CancellationToken stop)
    {
        if (options.Positionals.Count == 0) throw new ArgumentException("No remote specified.");
        var releaseOperation = options.Command == "release" ? options.Positionals.ElementAtOrDefault(1) : null;
        if (options.Command == "work")
        {
            var operation = options.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("No operation specified.");
            if (operation is not ("list" or "view" or "create" or "propose" or "edit" or "delete" or "comment" or "update" or "complete" or "activate" or "perms"))
                throw new InvalidOperationException("Invalid operation");
            if (operation is not ("list" or "create" or "propose") && options.DocId is null)
                throw new ArgumentException("No document ID specified.");
            if (operation is "create" or "propose" && string.IsNullOrEmpty(options.Title))
                throw new ArgumentException("No title specified.");
        }
        if (options.Command == "release")
        {
            if (releaseOperation is null) throw new ArgumentException("No operation specified.");
            if (releaseOperation is not ("list" or "view" or "fetch" or "verify" or "create" or "latest" or "delete"))
                throw new InvalidOperationException("Invalid operation");
            if (releaseOperation is "view" or "create" or "latest" or "delete" ||
                releaseOperation == "fetch" && !options.Offline)
                if (options.Positionals.Count < 3) throw new ArgumentException("No target specified.");
            if (options.Local && releaseOperation != "create") throw new ArgumentException("--local applies only to release create.");
        }
        if (options.Command == "release" && (options.Offline || releaseOperation == "verify"))
        {
            var verified = await RngitReleaseClient.VerifyAsync(options.Positionals[0], RequiredSigner(options.Signer),
                options.Positionals.ElementAtOrDefault(2) ?? "all", stop).ConfigureAwait(false);
            PrintVerification(verified, options.Signer is not null, output);
            return verified.IsValid ? 0 : 1;
        }
        var clientConfiguration = RngitClientConfiguration.Load(options.Config);
        using var identity = options.Identity is null ? clientConfiguration.LoadOrCreateIdentity() : IdentityFile.Load(options.Identity);
        var remote = options.Command is "fork" or "mirror"
            ? options.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("No target specified.")
            : options.Positionals[0];
        var groupOnly = options.Command == "perms" && remote.TrimEnd('/').Split('/').Length == 4;
        var resolved = clientConfiguration.ResolveUrl(groupOnly ? remote.TrimEnd('/') + "/placeholder" : remote);
        var address = RngitAddress.Parse(resolved);
        if (options.Command == "release" && options.Local)
        {
            if (releaseOperation != "create") throw new ArgumentException("--local applies only to release create.");
            using var alternateSigner = options.Signer is null ? null : IdentityFile.Load(options.Signer);
            var signer = alternateSigner ?? identity;
            var notes = await ContentAsync(options, "", ".md", stop).ConfigureAwait(false);
            var created = await RngitReleaseClient.CreateAsync(null, identity, signer, resolved,
                options.Positionals.ElementAtOrDefault(2) ?? throw new ArgumentException("No release target specified."),
                options.Name, true, stop, notes).ConfigureAwait(false);
            output.WriteLine("Release manifest saved to " + created.ManifestPath);
            return 0;
        }
        await using var node = ReticulumNode.Open(options.RnsConfig, (_, message) => error.WriteLine(message));
        await node.StartAsync(stop).ConfigureAwait(false);
        using var client = await RngitManagementClient.ConnectAsync(node, identity, address.DestinationHash, options.Timeout, stop).ConfigureAwait(false);
        var data = new Dictionary<object, object?> { [0] = address.Repository };
        string path;
        switch (options.Command)
        {
            case "create":
                path = "/git/create";
                break;
            case "sync":
                path = "/git/sync";
                break;
            case "fork" or "mirror":
                path = "/git/" + options.Command;
                var source = options.Positionals[0];
                data["source"] = source.StartsWith("rns://", StringComparison.OrdinalIgnoreCase) ? clientConfiguration.ResolveUrl(source) : source;
                break;
            case "perms":
                path = "/mgmt/perms";
                data["operation"] = groupOnly ? "gperms" : "rperms";
                if (groupOnly) { data.Remove(0); data[2] = address.Repository.Split('/')[0]; }
                await EditPermissionsAsync(client, path, data, options, output, stop).ConfigureAwait(false);
                return 0;
            case "work":
                return await WorkAsync(client, data, identity, options, output, stop).ConfigureAwait(false);
            case "release":
                return await ReleaseAsync(client, data, identity, resolved, options, output, stop).ConfigureAwait(false);
            default:
                throw new ArgumentException("Unknown command: " + options.Command);
        }
        var response = await client.RequestAsync(path, data, stop).ConfigureAwait(false);
        RngitManagementClient.Decode(response.Data);
        output.WriteLine(options.Command switch
        {
            "create" => $"Repository {address.Repository} created",
            "sync" => "Repository synced",
            _ => $"Repository {options.Command}ed to {address.Repository}",
        });
        return 0;
    }

    private static async Task EditPermissionsAsync(RngitManagementClient client, string path,
        Dictionary<object, object?> data, Options options, TextWriter output, CancellationToken stop)
    {
        data["step"] = "get";
        var current = RngitManagementClient.Decode((await client.RequestAsync(path, data, stop).ConfigureAwait(false)).Data);
        var content = await ContentAsync(options, RngitWire.Text(current, "content") ?? "", ".allowed", stop).ConfigureAwait(false);
        data["step"] = "set";
        data["content"] = content;
        RngitManagementClient.Decode((await client.RequestAsync(path, data, stop).ConfigureAwait(false)).Data);
        output.WriteLine("Permissions updated for " + (RngitWire.Text(data, 0) ?? RngitWire.Text(data, 2)));
    }

    private static async Task<int> WorkAsync(RngitManagementClient client, Dictionary<object, object?> data,
        Identity identity, Options options, TextWriter output, CancellationToken stop)
    {
        var operation = options.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("No operation specified.");
        if (operation == "update") operation = "comment";
        if (operation == "delete" && !await ConfirmAsync(options, output, $"delete {options.Scope} work document #{options.DocId}", stop).ConfigureAwait(false))
        { output.WriteLine("Deletion cancelled"); return 0; }
        data["operation"] = operation;
        data["scope"] = options.Scope;
        if (options.DocId is { } docId) data["doc_id"] = docId;
        if (options.Title is { } title) data["title"] = title;
        if (operation == "perms")
        {
            await EditPermissionsAsync(client, "/mgmt/work", data, options, output, stop).ConfigureAwait(false);
            return 0;
        }
        if (operation is "create" or "propose" or "edit" or "comment")
        {
            var initial = "";
            if (operation == "edit")
            {
                data["operation"] = "view";
                var document = RngitManagementClient.Decode((await client.RequestAsync("/mgmt/work", data, stop).ConfigureAwait(false)).Data);
                initial = RngitWire.Text(document, "content") ?? throw new InvalidDataException("Work document content missing.");
                if (options.Title is null) data["title"] = RngitWire.Text(RngitWire.Get(document, "meta"), "title");
                data["operation"] = operation;
            }
            var content = (await ContentAsync(options, initial, ".md", stop).ConfigureAwait(false)).Trim();
            data["content"] = content;
            data["format"] = "markdown";
            if (operation != "comment") data["signature"] = identity.Sign(Encoding.UTF8.GetBytes(content));
        }
        var result = RngitManagementClient.Decode((await client.RequestAsync("/mgmt/work", data, stop).ConfigureAwait(false)).Data);
        if (operation == "list") PrintWorkList(result, options.Scope, output);
        else if (operation == "view") PrintWorkDocument(result, options.Scope, output);
        else if (operation is "create" or "propose")
            output.WriteLine($"Work document created as {RngitWire.Text(result, "scope")} #{RngitWire.Get(result, "id")}");
        else if (operation == "comment") output.WriteLine($"Update #{RngitWire.Get(result, "id")} added to {options.Scope} document #{options.DocId}");
        else if (operation is "complete" or "activate") output.WriteLine($"Work document #{RngitWire.Get(result, "id")} {(operation == "complete" ? "completed" : "activated")}");
        else output.WriteLine($"Work document {options.Scope} #{options.DocId} {(operation == "edit" ? "updated" : "deleted")}");
        return 0;
    }

    private static void PrintWorkList(object? result, string scope, TextWriter output)
    {
        var scopes = scope == "all" ? new[] { "active", "completed", "proposed" } : [scope];
        var count = 0;
        foreach (var name in scopes)
        {
            if (RngitWire.Get(result, name) is not object?[] { Length: > 0 } documents)
            {
                if (scope != "all") output.WriteLine($"No {name} work documents found.");
                continue;
            }
            count += documents.Length;
            var heading = "\n" + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name) + " documents";
            output.WriteLine(heading);
            output.WriteLine(new string('=', heading.Length));
            output.WriteLine();
            output.WriteLine($"{"ID",-4} {"Title",-30} {"Author",-17} {"Created",-18} Comments");
            output.WriteLine(new string('-', 80));
            foreach (var doc in documents)
            {
                var title = RngitWire.Text(doc, "title") ?? "Untitled";
                if (title.Length > 29) title = title[..29] + "\u2026";
                var author = RngitWire.Text(doc, "author") ?? "";
                author = author[..Math.Min(author.Length, 16)] + "\u2026";
                output.WriteLine($"{RngitWire.Get(doc, "id"),-4} {title,-30} {author,-17} {Timestamp(RngitWire.Get(doc, "created"), false),-18} {RngitWire.Get(doc, "comments")}");
            }
            output.WriteLine();
        }
        if (scope == "all" && count == 0) output.WriteLine("No work documents found.");
    }

    private static void PrintWorkDocument(object? document, string scope, TextWriter output)
    {
        var meta = RngitWire.Get(document, "meta");
        var content = RngitWire.Text(document, "content") ?? throw new InvalidDataException("Missing work content.");
        var author = (RngitWire.Text(meta, "author") ?? "") + " (not locally validated)";
        var signature = "Document not signed";
        if (RngitWire.Get(meta, "signature") is byte[] sig && RngitWire.Get(meta, "identity") is byte[] publicKey)
        {
            using var signer = Identity.FromPublicKey(publicKey);
            signature = signer.Verify(sig, Encoding.UTF8.GetBytes(content)) ? "Valid" : "Not valid";
            if (signature == "Valid") author = "<" + signer.HexHash + ">";
        }
        var title = $"{RngitWire.Text(meta, "title")} (#{RngitWire.Get(document, "id")})";
        output.WriteLine(title);
        output.WriteLine(new string('=', title.Length));
        output.WriteLine($"Author    : {author}\nSignature : {signature}\nStatus    : {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(scope)}");
        output.WriteLine($"Created   : {Timestamp(RngitWire.Get(meta, "created"), true)}\nEdited    : {Timestamp(RngitWire.Get(meta, "edited"), true)}");
        output.WriteLine($"Format    : {RngitWire.Text(meta, "format")}");
        var comments = RngitWire.Get(document, "comments") as object?[] ?? [];
        output.WriteLine($"Updates   : {comments.Length}\n\n{content}");
        if (comments.Length > 0) output.WriteLine("\nUpdates\n=======");
        foreach (var comment in comments)
        {
            var heading = $"#{RngitWire.Get(comment, "id")} by {RngitWire.Text(comment, "author")} at {Timestamp(RngitWire.Get(comment, "created"), true)}";
            output.WriteLine("\n" + heading);
            output.WriteLine(new string('-', heading.Length));
            output.WriteLine(RngitWire.Text(comment, "content"));
        }
        output.WriteLine();
    }

    private static string Timestamp(object? value, bool seconds)
    {
        if (value is null) return "unknown";
        var timestamp = value switch
        {
            long number => (double)number,
            int number => number,
            double number => number,
            float number => number,
            _ => throw new InvalidDataException("Invalid remote timestamp."),
        };
        if (!double.IsFinite(timestamp)) throw new InvalidDataException("Invalid remote timestamp.");
        return timestamp == 0 ? "unknown" : DateTimeOffset.FromUnixTimeSeconds(checked((long)Math.Floor(timestamp)))
            .LocalDateTime.ToString(seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReleaseAsync(RngitManagementClient client, Dictionary<object, object?> data,
        Identity identity, string remote, Options options, TextWriter output, CancellationToken stop)
    {
        var operation = options.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("No operation specified.");
        data["operation"] = operation;
        if (options.Positionals.ElementAtOrDefault(2) is { } tagTarget) data["tag"] = tagTarget;
        if (operation == "delete" && !await ConfirmAsync(options, output, "delete release " + RngitWire.Text(data, "tag"), stop).ConfigureAwait(false))
        { output.WriteLine("Deletion cancelled"); return 0; }
        if (operation == "latest" && !await ConfirmAsync(options, output, "set " + RngitWire.Text(data, "tag") + " as the latest release", stop).ConfigureAwait(false))
        { output.WriteLine("Update cancelled"); return 0; }
        if (operation == "create")
        {
            using var alternateSigner = options.Signer is null ? null : IdentityFile.Load(options.Signer);
            var signer = alternateSigner ?? identity;
            var notes = await ContentAsync(options, "", ".md", stop).ConfigureAwait(false);
            var created = await RngitReleaseClient.CreateAsync(client, identity, signer, remote,
                options.Positionals.ElementAtOrDefault(2) ?? throw new ArgumentException("No release target specified."),
                options.Name, false, stop, notes).ConfigureAwait(false);
            output.WriteLine($"Release {created.Tag} created");
            return 0;
        }
        if (operation == "fetch")
        {
            var target = options.Positionals.ElementAtOrDefault(2) ?? throw new ArgumentException("No target specified.");
            if (!target.Contains(':')) throw new ArgumentException("Invalid release specification.");
            var verified = await RngitReleaseClient.FetchAsync(client, remote, target, Directory.GetCurrentDirectory(),
                RequiredSigner(options.Signer), stop).ConfigureAwait(false);
            PrintVerification(verified, options.Signer is not null, output);
            return verified.IsValid ? 0 : 1;
        }
        var result = RngitManagementClient.Decode((await client.RequestAsync("/mgmt/release", data, stop).ConfigureAwait(false)).Data);
        if (operation == "list")
        {
            var releases = RngitWire.Get(result, "releases") as object?[] ?? result as object?[] ?? [];
            if (releases.Length == 0) output.WriteLine("No releases for this repository");
            else
            {
                output.WriteLine($"{"Tag",-10} {"Status",-10} {"Created",-17} {"Objs",-5} Notes");
                output.WriteLine(new string('-', 80));
                foreach (var release in releases)
                {
                    var tag = Truncate(RngitWire.Text(release, "tag") ?? "unknown", 10);
                    var status = Truncate(RngitWire.Text(release, "status") ?? "unknown", 9);
                    var preview = Truncate((RngitWire.Text(release, "preview") ?? "").Split('\n')[0].TrimEnd('\r'), 34);
                    output.WriteLine($"{tag,-10} {status,-10} {Timestamp(RngitWire.Get(release, "created"), false),-17} {RngitWire.Get(release, "artifacts"),-5} {preview}");
                }
                if (RngitWire.Text(result, "latest") is { Length: > 0 } latest)
                    output.WriteLine("\nThe latest release is: " + latest);
            }
        }
        else if (operation == "view")
        {
            output.WriteLine("Release : " + (RngitWire.Text(result, "tag") ?? RngitWire.Text(data, "tag")));
            output.WriteLine("Status  : " + (RngitWire.Text(result, "status") ?? "unknown"));
            var created = Timestamp(RngitWire.Get(result, "created"), true);
            if (created != "unknown") output.WriteLine("Created : " + created);
            output.WriteLine("Thanks  : " + (RngitWire.Get(result, "thanks") ?? 0));
            var notes = RngitWire.Get(result, "notes") is byte[] bytes ? Encoding.UTF8.GetString(bytes) : RngitWire.Text(result, "notes");
            if (!string.IsNullOrEmpty(notes)) output.WriteLine("\nRelease Notes\n=============\n\n" + notes);
            if (RngitWire.Get(result, "artifacts") is object?[] { Length: > 0 } artifacts)
            {
                var heading = $"Artifacts ({artifacts.Length})";
                output.WriteLine("\n" + heading + "\n" + new string('=', heading.Length));
                foreach (var artifact in artifacts)
                {
                    var size = RngitWire.Get(artifact, "size") switch
                    {
                        long number => number, int number => number, _ => throw new InvalidDataException("Invalid artifact size."),
                    };
                    output.WriteLine($" - {RngitWire.Text(artifact, "name") ?? "unknown"} ({(size == 0 ? "0 B" : RngitPageService.Size(size))})");
                }
            }
            output.WriteLine();
        }
        else output.WriteLine($"Release {RngitWire.Text(data, "tag")} {(operation == "delete" ? "deleted" : "set as latest")}");
        return 0;
    }

    private static string Truncate(string text, int length) => string.Concat(text.EnumerateRunes().Take(length));

    private static byte[]? RequiredSigner(string? signer)
    {
        if (signer is null) return null;
        if (CliSupport.TryParseHash(signer, out var hash)) return hash;
        using var identity = IdentityFile.Load(signer);
        return identity.Hash.ToArray();
    }

    private static void PrintVerification(RngitReleaseVerification verified, bool requiredSigner, TextWriter output)
    {
        output.WriteLine($"Release {verified.Manifest.Name} {verified.Manifest.Version}");
        output.WriteLine($"Signature valid for identity <{Convert.ToHexStringLower(verified.Manifest.SignerHash)}>");
        output.WriteLine(requiredSigner ? "Signer matches the explicitly required identity." : "Publisher trust not established: the embedded signer key is not a trust anchor.");
        foreach (var artifact in verified.Artifacts)
            output.WriteLine($"{artifact.Name}: {(artifact.IsValid ? "Valid" : "Invalid: " + artifact.Error)}");
    }

    private static async Task<string> ContentAsync(Options options, string initial, string extension, CancellationToken stop)
    {
        if (options.Content is { } file)
        {
            if (new FileInfo(file).Length > 256 * 1024) throw new IOException("Content exceeds 256 KiB.");
            return await File.ReadAllTextAsync(file, new UTF8Encoding(false, true), stop).ConfigureAwait(false);
        }
        if (!options.AllowExec) throw new ArgumentException("Editor execution requires explicit --allow-exec, or provide --content <file>.");
        var scratch = Path.Combine(Path.GetTempPath(), "dotreticulum-editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var templates = new RngitTemplates(scratch, timeout: TimeSpan.FromMinutes(5), diagnostic: message => Console.Error.WriteLine(message));
            return await templates.EditAsync(initial, extension, true, stop).ConfigureAwait(false)
                ?? throw new IOException("Editor failed or was cancelled; no remote update sent.");
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task<bool> ConfirmAsync(Options options, TextWriter output, string action, CancellationToken stop)
    {
        if (options.Yes) return true;
        output.Write($"Are you sure you want to {action}? [y/N]: ");
        await output.FlushAsync(stop).ConfigureAwait(false);
        return string.Equals((await Console.In.ReadLineAsync(stop).ConfigureAwait(false))?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Options
    {
        internal string Command = "node";
        internal List<string> Positionals = [];
        internal string? Config, RnsConfig, Identity, Title, Signer, Name, Content;
        internal string Scope = "active";
        internal long? DocId;
        internal bool Help, Version, PrintIdentity, Interactive, Service, Offline, Local, AllowExec, AllowExternal, Yes;
        internal int Verbosity;
        internal TimeSpan Timeout = TimeSpan.FromHours(2);
    }

    private static Options Parse(string[] args)
    {
        var result = new Options();
        var start = 0;
        if (args.Length > 0 && args[0] is "node" or "create" or "fork" or "mirror" or "sync" or "perms" or "work" or "release")
        { result.Command = args[0]; start = 1; }
        for (var i = start; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
            void Only(params string[] commands)
            {
                if (!commands.Contains(result.Command, StringComparer.Ordinal))
                    throw new ArgumentException($"Option {args[i]} does not apply to {result.Command}.");
            }
            switch (args[i])
            {
                case "-h" or "--help": result.Help = true; break;
                case "--version": result.Version = true; break;
                case "--config": result.Config = Next(); break;
                case "--rnsconfig": result.RnsConfig = Next(); break;
                case "--identity": Only("create", "fork", "mirror", "sync", "perms", "work", "release"); result.Identity = Next(); break;
                case "-i" when result.Command == "node": result.Interactive = true; break;
                case "-i": result.Identity = Next(); break;
                case "--interactive": Only("node"); result.Interactive = true; break;
                case "-p" or "--print-identity": Only("node"); result.PrintIdentity = true; break;
                case "-s" when result.Command == "node": result.Service = true; break;
                case "--service": Only("node"); result.Service = true; break;
                case "-s" or "--signer": Only("release"); result.Signer = Next(); break;
                case "-n" or "--name": Only("release"); result.Name = Next(); break;
                case "-t" or "--title": Only("work"); result.Title = Next(); break;
                case "-d" or "--id": Only("work"); result.DocId = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--scope": Only("work", "mirror"); result.Scope = Next(); break;
                case "-o" or "--offline": Only("release"); result.Offline = true; break;
                case "-L" or "--local": Only("release"); result.Local = true; break;
                case "--content": Only("perms", "work", "release"); result.Content = Next(); break;
                case "--allow-exec": Only("node", "perms", "work", "release"); result.AllowExec = true; break;
                case "--allow-external": Only("node"); result.AllowExternal = true; break;
                case "--yes": Only("work", "release"); result.Yes = true; break;
                case "-v" or "--verbose": result.Verbosity++; break;
                case "-q" or "--quiet": result.Verbosity--; break;
                case var flags when flags.Length > 2 && flags[0] == '-' && flags.Skip(1).All(c => c is 'v' or 'q'):
                    if (flags.Length > 33) throw new ArgumentException("Too many verbosity flags.");
                    result.Verbosity += flags.Skip(1).Sum(c => c == 'v' ? 1 : -1);
                    break;
                case "--timeout":
                    var seconds = double.Parse(Next(), CultureInfo.InvariantCulture);
                    if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 4294967) throw new ArgumentException("Timeout must be positive and bounded.");
                    result.Timeout = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException("Unknown option: " + args[i]);
                    result.Positionals.Add(args[i]);
                    break;
            }
        }
        if (result.Scope is not ("active" or "completed" or "proposed" or "all")) throw new ArgumentException("Invalid work scope.");
        if (result.DocId < 0) throw new ArgumentException("Document ID must be nonnegative.");
        if (result.Command == "node" && result.Positionals.Count > 0) throw new ArgumentException("Unknown command: " + result.Positionals[0]);
        if (!result.Help && !result.Version)
        {
            var count = result.Positionals.Count;
            var validCount = result.Command switch
            {
                "node" => count == 0,
                "fork" or "mirror" => count == 2,
                "work" => count <= 2,
                "release" => count <= 3,
                _ => count == 1,
            };
            if (!validCount) throw new ArgumentException("Invalid argument count. " + Usage);
            if (result.Command != "node" && (result.PrintIdentity || result.Service || result.Interactive || result.AllowExternal))
                throw new ArgumentException("Node options require the node command.");
            if (result.Command != "release" && (result.Signer is not null || result.Name is not null || result.Offline || result.Local))
                throw new ArgumentException("Release options require the release command.");
            if (result.Command != "work" && (result.DocId is not null || result.Title is not null))
                throw new ArgumentException("Work options require the work command.");
            if (result.Interactive)
                throw new ArgumentException("Python's interactive runtime is not available in the native node. Use explicit management commands.");
        }
        return result;
    }
}
