using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rngit;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnid;

/// <summary>Native, bounded RNS 1.5.7 identity, RFE and RSG/RSM utility.</summary>
public static class RnidApp
{
    public const int EncryptionChunkBytes = 16 * 1024 * 1024;
    public const int DecryptionChunkBytes = EncryptionChunkBytes + 96;
    private const string Usage = """
        Reticulum Identity & Encryption Utility (experimental DotReticulum)
        usage: rnid [-i identity | -g path | -m public | -M private] [options]
          --show path                 Legacy identity hash/public-key display
          -g, --generate path         Generate a raw private identity (owner-only on Unix)
          -m, --import-pub data       Import raw file, hex, base32 or URL-safe base64 public keys
          -M, --import-prv data       Import raw file, hex, base32 or URL-safe base64 private keys
          -x, --export-pub            Export public keys
          -X, --export-prv            Export private keys (explicit disclosure)
          -p, --print-identity        Display identity; -P also displays private keys
          -H, --hash aspects         Display destination hash
          -a, --announce [aspects]    Announce destination (default rns.id)
          -R, --request              Request unknown identity from the network
          -N, --no-cache             Do not use cached/network identity information
          --config path              Reticulum configuration directory
          -t seconds                 Network timeout (default 15; maximum 3600)
          -s, --sign paths...         Sign files; --raw creates legacy raw signatures
          -V, --validate paths...     Verify RSG files or embedded RSM messages
          -S, --sign-message [text]   Sign embedded message; -r reads UTF-8 file
          -E, --embed-meta [path]     Embed ConfigObj metadata; --meta-spec validates it
          --meta                     Display verified RSM metadata
          -e, --encrypt paths...      Encrypt files to .rfe
          -d, --decrypt paths...      Decrypt .rfe files
          -w, --write path            Output path (public identities gain .pub)
          -f, --force                 Explicitly overwrite existing output
          -B, -b, -U, -F              Base32, URL-safe base64, base256, hex RSG output
          -I, --stdin                Read operation input from stdin (also accepts -)
          -O, --stdout               Write operation bytes to stdout; diagnostics to stderr
          --allow-editor             Explicitly authorize an editor for omitted message/metadata
          --editor executable        Editor path (otherwise EDITOR); no shell
          --editor-args arguments    Explicit editor arguments (POSIX-style quoted tokens)
          --editor-timeout seconds   Editor deadline (default 120; maximum 3600)
          -v, -q                     Compatibility verbosity flags
          --version                  Print reference compatibility version
        Limits: 16 MiB raw/embedded message, 1 MiB signature/metadata, 32 metadata levels.
        Current detached signatures and RFE files use bounded streaming for arbitrarily large files.
        """;

    public static async Task<int> RunAsync(string[] args, Stream input, Stream binaryOutput, TextWriter output,
        TextWriter error, CancellationToken stop)
    {
        var legacy = args.Length == 2 && args[0] is "--show" or "--generate";
        ReticulumNode? node = null;
        Identity? identity = null;
        try
        {
            var options = RnidOptions.Parse(args);
            if (options.Has("--help")) { await output.WriteLineAsync(Usage).ConfigureAwait(false); return 0; }
            if (options.Has("--version")) { await output.WriteLineAsync("rnid 1.5.7").ConfigureAwait(false); return 0; }
            if (args.Length == 0) { await output.WriteLineAsync(Usage).ConfigureAwait(false); return 2; }
            var log = options.Has("--stdout") ? error : output;
            async Task<ReticulumNode> GetNodeAsync()
            {
                if (node is not null) return node;
                node = ReticulumNode.Open(options.Get("--config"), CliSupport.Logger(error, options.Has("--stdout") ? -1 : 4 + options.Verbosity));
                await node.StartAsync(stop).ConfigureAwait(false);
                return node;
            }
            stop.ThrowIfCancellationRequested();
            if (options.Get("--generate") is { } generate)
            {
                generate = Expand(generate);
                identity = Identity.Generate();
                await SavePrivateAsync(identity, generate, options.Has("--force"), stop).ConfigureAwait(false);
                if (!legacy) await log.WriteLineAsync($"New identity <{identity.HexHash}> written to {generate}").ConfigureAwait(false);
                if (OperatingSystem.IsWindows())
                    await error.WriteLineAsync("Warning: the identity file contains your private key; restrict its Windows ACLs.").ConfigureAwait(false);
            }
            else if (options.Get("--show") is { } show) identity = await LoadPrivateAsync(Expand(show), stop).ConfigureAwait(false);
            else if (options.Get("--identity") is { } requested)
            {
                if (File.Exists(Expand(requested)))
                {
                    try { identity = await LoadPrivateAsync(Expand(requested), stop).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                    { throw new RnidFailure(8, $"Could not load Identity from specified file: {ex.Message}"); }
                    await log.WriteLineAsync($"Loaded Identity <{identity.HexHash}> from {Expand(requested)}").ConfigureAwait(false);
                }
                else if (!options.Has("--no-cache") && CliSupport.TryParseHash(requested, out var hash))
                {
                    var runtime = await GetNodeAsync().ConfigureAwait(false);
                    identity = runtime.Router.Engine.Known.Recall(hash) ?? runtime.Router.Engine.Known.Recall(hash, fromIdentityHash: true);
                    var wasRecalled = identity is not null;
                    if (identity is null && options.Has("--request"))
                    {
                        var defaultDestination = Destination.ComputeHashFromNameAndIdentity("rns.id", hash);
                        await runtime.Router.RequestPathAsync(hash, cancellationToken: stop).ConfigureAwait(false);
                        await runtime.Router.RequestPathAsync(defaultDestination, cancellationToken: stop).ConfigureAwait(false);
                        var until = DateTime.UtcNow.AddSeconds(options.Timeout);
                        while (identity is null && DateTime.UtcNow < until)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(100), stop).ConfigureAwait(false);
                            identity = runtime.Router.Engine.Known.Recall(hash) ?? runtime.Router.Engine.Known.Recall(hash, fromIdentityHash: true);
                            if (runtime.Completion.IsCompleted) await runtime.Completion.ConfigureAwait(false);
                        }
                        if (identity is null) await log.WriteLineAsync("Identity request timed out").ConfigureAwait(false);
                    }
                    if (identity is not null)
                    {
                        var suffix = identity.Hash.AsSpan().SequenceEqual(hash) ? "" : $" for destination <{requested}>";
                        await log.WriteLineAsync($"{(wasRecalled ? "Recalled" : "Received")} Identity <{identity.HexHash}>{suffix}{(wasRecalled ? "" : " from the network")}").ConfigureAwait(false);
                        await runtime.RetainIdentityAsync(identity.Hash, stop).ConfigureAwait(false);
                        await runtime.PersistAsync(stop).ConfigureAwait(false);
                    }
                }
            }
            else if (options.Has("--import-pub") || options.Has("--import-prv"))
            {
                var privateImport = options.Has("--import-prv");
                var value = privateImport ? options.Get("--import-prv")! : options.Get("--import-pub")!;
                var key = await ImportAsync(value, stop).ConfigureAwait(false);
                try { identity = privateImport ? Identity.FromPrivateKey(key) : Identity.FromPublicKey(key); }
                catch (ArgumentException ex) { throw new RnidFailure(8, $"Could not create Reticulum identity from specified data: {ex.Message}"); }
                finally { if (privateImport) CryptographicOperations.ZeroMemory(key); }
                await log.WriteLineAsync($"Reticulum Identity imported from {ImportDescription(value)}").ConfigureAwait(false);
            }

            if (legacy && identity is not null)
            {
                await output.WriteLineAsync($"Identity hash: {identity.HexHash}").ConfigureAwait(false);
                await output.WriteLineAsync($"Public key:    {Convert.ToHexStringLower(identity.ExportPublicKey())}").ConfigureAwait(false);
                return 0;
            }
            var needsIdentity = options.Operation is "--sign" or "--sign-message" or "--encrypt" or "--decrypt"
                || new[] { "--print-identity", "--export-pub", "--export-prv", "--announce", "--write" }.Any(options.Has);
            if (needsIdentity && identity is null) throw new RnidFailure(2, "Could not get working identity");
            var infoFormat = options.Format is "base32" or "base64" ? options.Format : "hex";
            if (options.Has("--print-identity"))
            {
                await log.WriteLineAsync($"Identity Hash : <{identity!.HexHash}>").ConfigureAwait(false);
                await log.WriteLineAsync($"Public Key    : {RnidEncoding.Encode(identity.ExportPublicKey(), infoFormat)}").ConfigureAwait(false);
                if (identity.HasPrivateKey)
                    await log.WriteLineAsync("Private Key   : " + (options.Has("--print-private") ? ExportPrivate(identity, infoFormat) : "Hidden")).ConfigureAwait(false);
            }
            if (options.Has("--export-pub"))
                await log.WriteLineAsync("Public Identity Keys  : " + RnidEncoding.Encode(identity!.ExportPublicKey(), infoFormat)).ConfigureAwait(false);
            if (options.Has("--export-prv"))
            {
                RequirePrivate(identity!);
                await log.WriteLineAsync("Private Identity Keys : " + ExportPrivate(identity!, infoFormat)).ConfigureAwait(false);
            }
            if (options.Get("--hash") is { } aspects)
            {
                var hash = identity?.Hash ?? (CliSupport.TryParseHash(options.Get("--identity"), out var supplied) ? supplied : throw new RnidFailure(8, "Invalid identity"));
                var destination = Destination.ComputeHashFromNameAndIdentity(aspects, hash);
                await log.WriteLineAsync($"The {aspects} destination for this Identity is <{Convert.ToHexStringLower(destination)}>").ConfigureAwait(false);
                if (identity is not null)
                    await log.WriteLineAsync($"The full destination specifier is <{aspects}.{identity.HexHash}:{Convert.ToHexStringLower(destination)}>").ConfigureAwait(false);
            }
            if (options.Get("--announce") is { } announce)
            {
                if (!announce.Contains('.')) throw new RnidFailure(9, "Invalid destination aspects specified");
                RequirePrivate(identity!);
                var runtime = await GetNodeAsync().ConfigureAwait(false);
                var (app, announceAspects) = Destination.SplitFullName(announce);
                var destination = Destination.Create(DestinationType.Single, app, announceAspects, identity!);
                using var local = new LocalDestination(identity!, destination);
                runtime.Router.RegisterLocalDestination(local);
                try
                {
                    await log.WriteLineAsync($"Announcing {announce} destination <{Convert.ToHexStringLower(destination.Hash)}> for identity <{identity!.HexHash}>").ConfigureAwait(false);
                    await runtime.Router.AnnounceAsync(local, cancellationToken: stop).ConfigureAwait(false);
                    await Task.Delay(250, stop).ConfigureAwait(false);
                }
                finally { runtime.Router.DeregisterLocalDestination(local); }
            }
            if (options.Operation == "--sign-message")
            {
                await SignMessageAsync(options, identity!, input, binaryOutput, log, stop).ConfigureAwait(false);
                return 0;
            }
            if (options.Operation is { } operation)
            {
                var paths = options.Paths.Count == 0 && options.Has("--stdin") ? ["-"] : options.Paths.ToArray();
                foreach (var path in paths)
                {
                    stop.ThrowIfCancellationRequested();
                    if (operation == "--validate")
                        await ValidateAsync(options, identity, path, input, log, stop).ConfigureAwait(false);
                    else if (operation == "--sign")
                        await SignAsync(options, identity!, path, input, binaryOutput, log, stop).ConfigureAwait(false);
                    else
                        await CryptAsync(options, identity!, path, input, binaryOutput, log, operation == "--decrypt", stop).ConfigureAwait(false);
                }
                return 0;
            }
            if (options.Get("--write") is { } write)
            {
                write = Expand(write);
                if (options.Has("--export-prv") && identity!.HasPrivateKey)
                {
                    await SavePrivateAsync(identity, write, options.Has("--force"), stop).ConfigureAwait(false);
                    await log.WriteLineAsync($"Wrote private identity to {write}").ConfigureAwait(false);
                }
                else
                {
                    if (!write.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)) write += ".pub";
                    await WriteFileAsync(write, identity!.ExportPublicKey(), options.Has("--force"), stop).ConfigureAwait(false);
                    await log.WriteLineAsync($"Wrote public identity to {write}").ConfigureAwait(false);
                }
            }
            if (options.Values.Keys.All(k => k is "--identity" or "--import-pub" or "--import-prv" or "--config" or "--no-cache" or "--request" or "-t" or "-v" or "-q"))
                await log.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }
        catch (RnidFailure ex)
        {
            await (legacy || args.Contains("--stdout") || args.Contains("-O") || ex.Code == 2 && ex.Message.StartsWith("argument", StringComparison.Ordinal) ? error : output).WriteLineAsync(ex.Message).ConfigureAwait(false);
            return legacy ? 1 : ex.Code;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await error.WriteLineAsync("rnid: Operation cancelled.").ConfigureAwait(false);
            return 255;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidOperationException or CryptographicException)
        {
            await error.WriteLineAsync($"rnid: {ex.Message}").ConfigureAwait(false);
            return legacy ? 1 : ex is IOException or UnauthorizedAccessException ? 253 : 254;
        }
        finally
        {
            if (node is not null) await node.DisposeAsync().ConfigureAwait(false);
            identity?.Dispose();
        }
    }

    private static async Task SignAsync(RnidOptions options, Identity identity, string target, Stream input,
        Stream binaryOutput, TextWriter log, CancellationToken stop)
    {
        RequirePrivate(identity);
        var path = Expand(target);
        await using var source = target == "-" ? null : OpenInput(path);
        var stream = source ?? input;
        byte[] signature;
        if (options.Has("--raw"))
        {
            var data = await ReadBoundedAsync(stream, RngitSignatures.MaximumMessageBytes, stop).ConfigureAwait(false);
            try { signature = identity.Sign(data); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        else
        {
            var hash = await SHA256.HashDataAsync(stream, stop).ConfigureAwait(false);
            signature = RngitSignatures.CreateRsgFromHash(identity, hash);
        }
        if (options.Format != "bin" && !options.Has("--raw"))
            await log.WriteLineAsync($"\n{RnidEncoding.Wrap(RnidEncoding.Encode(signature, options.Format))}\n").ConfigureAwait(false);
        else if (options.Has("--stdout"))
            await binaryOutput.WriteAsync(signature, stop).ConfigureAwait(false);
        else
        {
            var output = options.Get("--write") is { } write ? Expand(write) : target == "-" ? throw new RnidFailure(250, "No write path specified") : path + ".rsg";
            await WriteFileAsync(output, signature, options.Has("--force"), stop).ConfigureAwait(false);
        }
        await log.WriteLineAsync($"Signed file {path} with <{identity.HexHash}>").ConfigureAwait(false);
    }

    private static async Task SignMessageAsync(RnidOptions options, Identity identity, Stream input,
        Stream binaryOutput, TextWriter log, CancellationToken stop)
    {
        RequirePrivate(identity);
        if (options.Format == "bin" && !options.Has("--write") && !options.Has("--stdout"))
            throw new RnidFailure(250, "No write path specified");
        byte[] message;
        if (options.Get("--read") is { } read)
        {
            if (options.Get("--sign-message") is not null) throw new RnidFailure(250, "Both an input file and command-line provided message was specified, aborting");
            message = await ReadFileAsync(Expand(read), RngitSignatures.MaximumMessageBytes, stop).ConfigureAwait(false);
        }
        else if (options.Get("--sign-message") is { } text) message = Encoding.UTF8.GetBytes(text);
        else if (options.Has("--stdin")) message = await ReadBoundedAsync(input, RngitSignatures.MaximumMessageBytes, stop).ConfigureAwait(false);
        else message = await EditorAsync(options, stop).ConfigureAwait(false);
        if (message.Length == 0) throw new RnidFailure(250, "No message specified");
        _ = new UTF8Encoding(false, true).GetString(message);
        Dictionary<object, object?>? metadata = null;
        if (options.Has("--embed-meta"))
        {
            byte[] data;
            string? spec = null;
            if (options.Get("--embed-meta") is { } metaPath)
            {
                metaPath = Expand(metaPath);
                data = await ReadFileAsync(metaPath, 1024 * 1024, stop).ConfigureAwait(false);
                var specPath = options.Get("--meta-spec") is { } explicitSpec ? Expand(explicitSpec) : metaPath + ".spec";
                if (File.Exists(specPath)) spec = Encoding.UTF8.GetString(await ReadFileAsync(specPath, 1024 * 1024, stop).ConfigureAwait(false));
                else if (options.Get("--meta-spec") is not null) throw new RnidFailure(6, $"Metadata spec file {specPath} does not exist");
                await log.WriteLineAsync($"Embedding metadata from {metaPath}{(spec is null ? "" : $" using spec from {specPath}")}").ConfigureAwait(false);
            }
            else data = await EditorAsync(options, stop).ConfigureAwait(false);
            metadata = RnidMetadata.Parse(new UTF8Encoding(false, true).GetString(data), spec);
        }
        var signature = RngitSignatures.CreateRsg(identity, message, embed: true, metadata);
        if (options.Format != "bin")
            await log.WriteLineAsync($"\n{RnidEncoding.Wrap(RnidEncoding.Encode(signature, options.Format))}\n").ConfigureAwait(false);
        else if (options.Has("--stdout")) await binaryOutput.WriteAsync(signature, stop).ConfigureAwait(false);
        else
        {
            var path = Expand(options.Get("--write")!);
            if (!path.EndsWith(".rsm", StringComparison.Ordinal)) path += ".rsm";
            await WriteFileAsync(path, signature, options.Has("--force"), stop).ConfigureAwait(false);
            await log.WriteLineAsync($"Message signed with <{identity.HexHash}> saved to {path}").ConfigureAwait(false);
            return;
        }
        await log.WriteLineAsync($"Message signed with <{identity.HexHash}>").ConfigureAwait(false);
    }

    private static async Task ValidateAsync(RnidOptions options, Identity? identity, string target,
        Stream input, TextWriter output, CancellationToken stop)
    {
        var path = Expand(target);
        var embedded = path.EndsWith(".rsm", StringComparison.OrdinalIgnoreCase);
        var signaturePath = embedded || path.EndsWith(".rsg", StringComparison.OrdinalIgnoreCase) ? path : path + ".rsg";
        var filePath = path.EndsWith(".rsg", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
        var raw = target == "-" ? await ReadBoundedAsync(input, 4 * 1024 * 1024, stop).ConfigureAwait(false)
            : await ReadFileAsync(signaturePath, 4 * 1024 * 1024, stop).ConfigureAwait(false);
        byte[] signature;
        try { signature = RnidEncoding.DecodeSignature(raw); }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        { throw new RnidFailure(10, $"Invalid signature {signaturePath}: {ex.Message}"); }
        var extracted = RngitSignatures.ExtractRsg(signature);
        if (target == "-" && extracted?.ContainsKey("message") == true) embedded = true;
        byte[] required = identity?.Hash ?? [];
        if (required.Length == 0 && options.Get("--identity") is { } expected)
        {
            if (!CliSupport.TryParseHash(expected, out required)) throw new RnidFailure(8, "Invalid identity hash length");
        }
        if (signature.Length == 64)
        {
            if (identity is null) throw new RnidFailure(2, "Cannot validate legacy rsg signatures without an explicit required identity");
            var data = await ReadFileAsync(options.Get("--read") is { } read ? Expand(read) : filePath, RngitSignatures.MaximumMessageBytes, stop).ConfigureAwait(false);
            if (!identity.Verify(signature, data)) throw new RnidFailure(10, $"Invalid signature {signaturePath} for file {filePath}\nThis file was NOT signed by <{identity.HexHash}>");
            await output.WriteLineAsync($"Signature is valid, the file {filePath} was signed by <{identity.HexHash}>").ConfigureAwait(false);
            return;
        }
        byte[] digest;
        byte[]? message = null;
        if (embedded)
        {
            if (extracted?.GetValueOrDefault("message") is not byte[] embeddedMessage || embeddedMessage.Length == 0)
                throw new RnidFailure(10, $"No embedded message in {signaturePath}");
            message = embeddedMessage;
            digest = SHA256.HashData(message);
        }
        else
        {
            var dataPath = options.Get("--read") is { } read ? Expand(read) : filePath;
            await using var data = OpenInput(dataPath);
            digest = await SHA256.HashDataAsync(data, stop).ConfigureAwait(false);
        }
        var validation = RngitSignatures.ValidateRsgHash(signature, digest, required);
        // Explicit reference identities override envelope key selection, while still requiring its fields.
        if (identity is not null && extracted?.GetValueOrDefault("hashtype") is "sha256"
            && extracted.GetValueOrDefault("hash") is byte[] embeddedHash
            && extracted.GetValueOrDefault("meta") is Dictionary<object, object?> signingMeta
            && signingMeta.ContainsKey("signer") && signingMeta.ContainsKey("pubkey"))
            validation = new(identity.Verify(signature.AsSpan(0, 64), signature.AsSpan(64))
                && CryptographicOperations.FixedTimeEquals(embeddedHash, digest), extracted, identity.Hash, identity.ExportPublicKey());
        if (!validation.IsValid) throw new RnidFailure(10, $"Invalid signature {(embedded ? "in" : "")} {signaturePath}{(required.Length == 0 ? "" : $"\nThis file was NOT signed by <{Convert.ToHexStringLower(required)}>")}");
        var signer = Convert.ToHexStringLower(validation.SignerHash!);
        if (!embedded)
        {
            await output.WriteLineAsync($"Signature is valid, the file {filePath} was signed by <{signer}>").ConfigureAwait(false);
            return;
        }
        if (options.Has("--meta") && validation.SignedData?.GetValueOrDefault("meta") is Dictionary<object, object?> meta)
        {
            await output.WriteLineAsync("RSM Metadata\n============\n").ConfigureAwait(false);
            foreach (var entry in meta) await PrintMetadataAsync(output, entry.Key, entry.Value, 1).ConfigureAwait(false);
            await output.WriteLineAsync("\nValidation\n==========").ConfigureAwait(false);
        }
        await output.WriteLineAsync($"\nSignature is valid, the{(options.Has("--meta") ? "" : " following")} message was signed by <{signer}>{(options.Has("--meta") ? "" : ":")}\n").ConfigureAwait(false);
        if (options.Has("--meta")) await output.WriteLineAsync("Message\n=======\n").ConfigureAwait(false);
        await output.WriteLineAsync(new UTF8Encoding(false, true).GetString(message!)).ConfigureAwait(false);
    }

    private static async Task CryptAsync(RnidOptions options, Identity identity, string target, Stream input,
        Stream binaryOutput, TextWriter log, bool decrypt, CancellationToken stop)
    {
        if (decrypt) RequirePrivate(identity);
        var path = Expand(target);
        if (decrypt && target != "-" && !path.EndsWith(".rfe", StringComparison.Ordinal))
            throw new RnidFailure(7, $"The file {path} does not appear to be a Reticulum encrypted file");
        var outputPath = options.Get("--write") is { } write ? Expand(write)
            : target == "-" ? null : decrypt ? path[..^4] : path + ".rfe";
        if (!options.Has("--stdout") && string.IsNullOrEmpty(outputPath)) throw new RnidFailure(7, "Invalid output filename");
        if (outputPath is not null && Path.GetFullPath(outputPath) == Path.GetFullPath(path))
            throw new RnidFailure(250, "Input and output must be different files");
        await using var file = target == "-" ? null : OpenInput(path);
        var source = file ?? input;
        await using var destination = options.Has("--stdout") ? null : OpenOutput(outputPath!, options.Has("--force"));
        var sink = destination ?? binaryOutput;
        var buffer = new byte[decrypt ? DecryptionChunkBytes : EncryptionChunkBytes];
        long wrote = 0;
        try
        {
            while (true)
            {
                var count = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, stop).ConfigureAwait(false);
                if (count == 0)
                {
                    if (!decrypt && !options.Has("--stdout"))
                        await log.WriteAsync($"\rWrote {CliSupport.SizeString(wrote)} to {outputPath}   ").ConfigureAwait(false);
                    break;
                }
                var result = decrypt ? identity.Decrypt(buffer.AsSpan(0, count)) : identity.Encrypt(buffer.AsSpan(0, count));
                if (result is null || decrypt && result.Length == 0) throw new RnidFailure(12, "The provided identity could not decrypt the file");
                try { await sink.WriteAsync(result, stop).ConfigureAwait(false); wrote += result.Length; }
                finally { if (decrypt) CryptographicOperations.ZeroMemory(result); }
                if (!options.Has("--stdout"))
                    await log.WriteAsync($"\rWrote {CliSupport.SizeString(wrote)} to {outputPath}   ").ConfigureAwait(false);
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
        await log.WriteLineAsync(decrypt ? $"\nFile {path} decrypted to {outputPath ?? "STDOUT"}" : $"\nFile {path} encrypted for <{identity.HexHash}> to {outputPath ?? "STDOUT"}").ConfigureAwait(false);
    }

    private static async Task<byte[]> EditorAsync(RnidOptions options, CancellationToken stop)
    {
        if (!options.Has("--allow-editor")) throw new RnidFailure(252, "Editor execution requires explicit --allow-editor authorization");
        var editor = options.Get("--editor") ?? Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrWhiteSpace(editor)) throw new RnidFailure(252, "Could not launch editor: specify --editor or EDITOR");
        var duration = 120d;
        if (options.Get("--editor-timeout") is { } timeout
            && (!double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out duration)
                || !double.IsFinite(duration) || duration <= 0 || duration > 3600))
            throw new RnidFailure(250, "Editor timeout must be between 0 and 3600 seconds");
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".rnid-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "message.txt");
        try
        {
            await File.WriteAllTextAsync(path, "", new UTF8Encoding(false), stop).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var editorArguments = options.Get("--editor-args") is { } editorArgs ? CliSupport.ShellSplit(editorArgs) : [];
            editorArguments.Add(path);
            var result = await RngitProcess.RunAsync(editor, editorArguments, directory, timeout: TimeSpan.FromSeconds(duration),
                maxOutputBytes: 1024 * 1024, stop: stop).ConfigureAwait(false);
            if (result.Code != 0) throw new RnidFailure(252, $"Editor exited with error code {result.Code}: {result.Error}");
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new RnidFailure(252, "Editor output must not be a symbolic link");
            return await ReadFileAsync(path, RngitSignatures.MaximumMessageBytes, stop).ConfigureAwait(false);
        }
        finally
        {
            // Never recurse into editor-created paths or symlinks.
            File.Delete(path);
            try { Directory.Delete(directory); }
            catch (IOException) { }
        }
    }

    private static async Task PrintMetadataAsync(TextWriter writer, object key, object? value, int level)
    {
        if (level > 32) throw new FormatException("Metadata nesting exceeds 32 levels.");
        var indent = new string(' ', level * 2);
        if (value is Dictionary<object, object?> map)
        {
            await writer.WriteLineAsync($"d{indent}{key}:").ConfigureAwait(false);
            foreach (var item in map) await PrintMetadataAsync(writer, item.Key, item.Value, level + 1).ConfigureAwait(false);
            return;
        }
        if (key is "note" && value is null) return;
        var type = value switch { string => "s", byte[] => "b", object[] => "l", long or int => "i", float or double => "f", null => "N", _ => "u" };
        var text = value is byte[] bytes ? Convert.ToHexStringLower(bytes) : value is object[] list
            ? "[" + string.Join(", ", list.Select(v => v is string s ? $"'{s}'" : v?.ToString() ?? "None")) + "]"
            : value is bool boolean ? boolean ? "True" : "False" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "None";
        var lead = $"{type}{indent}{key}=";
        var points = text.EnumerateRunes().ToArray();
        for (var offset = 0; offset < Math.Max(1, points.Length); offset += 64)
            await writer.WriteLineAsync((offset == 0 ? lead : new string(' ', lead.Length))
                + string.Concat(points.Skip(offset).Take(64).Select(p => p.ToString()))).ConfigureAwait(false);
    }

    private static void RequirePrivate(Identity identity)
    {
        if (!identity.HasPrivateKey) throw new RnidFailure(4, "The identity does not hold a private key");
    }

    private static string ExportPrivate(Identity identity, string format)
    {
        var key = identity.ExportPrivateKey();
        try { return RnidEncoding.Encode(key, format); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static string Expand(string path) => path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;

    private static string ImportDescription(string value)
    {
        if (File.Exists(Expand(value))) return Expand(value);
        return value.Length == 128 ? "hex input" : value.Length == 104 ? "base32 input" : "base64 input";
    }

    private static async Task<byte[]> ImportAsync(string value, CancellationToken stop)
    {
        if (File.Exists(Expand(value)))
        {
            try { return await ReadFileAsync(Expand(value), 64, stop).ConfigureAwait(false); }
            catch (RnidFailure ex) when (ex.Code == 252) { throw new RnidFailure(8, "Identity file must be exactly 64 bytes"); }
        }
        foreach (var format in new[] { "hex", "base32", "base64" })
        {
            try
            {
                var bytes = RnidEncoding.Decode(value, format);
                if (bytes.Length == 64) return bytes;
            }
            catch (FormatException) { }
        }
        throw new RnidFailure(8, "Could not decode specified data to a valid Reticulum Identity");
    }

    private static async Task<Identity> LoadPrivateAsync(string path, CancellationToken stop)
    {
        byte[] bytes;
        try { bytes = await ReadFileAsync(path, 64, stop).ConfigureAwait(false); }
        catch (RnidFailure ex) when (ex.Code == 252) { throw new RnidFailure(8, "Identity file must be exactly 64 bytes"); }
        try
        {
            if (bytes.Length != 64) throw new InvalidDataException("Identity file must be exactly 64 bytes.");
            return Identity.FromPrivateKey(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static Task SavePrivateAsync(Identity identity, string path, bool force, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        if (!force)
        {
            if (File.Exists(path)) throw new RnidFailure(11, $"Identity file {path} already exists. Not overwriting.");
            IdentityFile.SaveNew(identity, path);
            return Task.CompletedTask;
        }
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try { IdentityFile.SaveNew(identity, staging); File.Move(staging, path, overwrite: true); }
        finally { File.Delete(staging); }
        return Task.CompletedTask;
    }

    private static FileStream OpenInput(string path)
    {
        if (!File.Exists(path)) throw new RnidFailure(6, $"The file \"{path}\" does not exist");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static FileStream OpenOutput(string path, bool force)
    {
        if (!force && File.Exists(path)) throw new RnidFailure(11, $"The output file \"{path}\" already exists, not overwriting");
        var options = new FileStreamOptions { Mode = force ? FileMode.Create : FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task<byte[]> ReadFileAsync(string path, int limit, CancellationToken stop)
    {
        await using var stream = OpenInput(path);
        return await ReadBoundedAsync(stream, limit, stop).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken stop)
    {
        if (stream.CanSeek && stream.Length - stream.Position > limit) throw new RnidFailure(252, $"Input exceeds the {limit} byte limit");
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)result.Length + 1)), stop).ConfigureAwait(false);
                if (count == 0) return result.ToArray();
                if (result.Length + count > limit) throw new RnidFailure(252, $"Input exceeds the {limit} byte limit");
                result.Write(buffer, 0, count);
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task WriteFileAsync(string path, byte[] bytes, bool force, CancellationToken stop)
    {
        await using var stream = OpenOutput(path, force);
        await stream.WriteAsync(bytes, stop).ConfigureAwait(false);
    }
}
