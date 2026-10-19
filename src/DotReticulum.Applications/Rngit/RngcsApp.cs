using System.Security.Cryptography;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

/// <summary>
/// Git SSH signing adapter. Upstream fixes the namespace to git and ignores -n, -O and the
/// verification -f allowed-signers file. Success proves the author/tagger's identity, NOT trust
/// granted by an allowed-signers policy. check-novalidate and find-principals do not verify crypto.
/// Input is bounded to 16 MiB; signatures to 1 MiB decoded / 2 MiB armored.
/// </summary>
public static class RngcsApp
{
    private const string Usage = "usage: rngcs [-h] -Y {sign,find-principals,check-novalidate,verify}\n"
        + "             [-n NAMESPACE] [-f KEYFILE] [-I PRINCIPAL] [-s SIGFILE]\n"
        + "             [-O SSH_OPTIONS]\n"
        + "             [file]\n";

    public static async Task<int> RunAsync(string[] args, Stream input, TextWriter output, TextWriter error,
        CancellationToken stop)
    {
        var operation = "";
        try
        {
            var options = Parse(args);
            operation = options.GetValueOrDefault("-Y") ?? "";
            if (options.ContainsKey("--help"))
            {
                await output.WriteAsync("""
                    usage: rngcs -Y {sign,find-principals,check-novalidate,verify} [-n namespace]
                                 [-f keyfile] [-I principal] [-s sigfile] [-O option] [file]
                    Git commit signer and validator
                    Compatibility: namespace is always git; -n, -O and verification -f are ignored.
                    Verification authenticates the author/tagger identity, not allowed-signers trust.
                    Limits: 16 MiB message; 1 MiB decoded / 2 MiB armored signature.

                    """).ConfigureAwait(false);
                return 0;
            }
            stop.ThrowIfCancellationRequested();
            if (operation == "sign") return await SignAsync(options, input, output, error, stop).ConfigureAwait(false);
            var path = options.GetValueOrDefault("-s");
            if (path is null || !File.Exists(path))
            {
                if (operation != "check-novalidate") await error.WriteLineAsync("Error: Signature file not found").ConfigureAwait(false);
                return 1;
            }
            var message = operation == "verify"
                ? await ReadBoundedAsync(input, RngitSignatures.MaximumMessageBytes, stop).ConfigureAwait(false) : [];
            RngitSignatures.SshSignature signature;
            byte[] raw;
            try
            {
                raw = await ReadFileAsync(path, RngitSignatures.MaximumArmoredCharacters, stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (operation != "check-novalidate")
                    await error.WriteLineAsync($"Error {(operation == "verify" ? "parsing signature" : "reading signature file")}: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
            try
            {
                signature = RngitSignatures.ParseSshSignature(RngitSignatures.UnarmorSshSignature(Encoding.UTF8.GetString(raw)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                if (operation != "check-novalidate")
                    await error.WriteLineAsync($"Error parsing {(operation == "verify" ? "signature" : "SSH signature")}: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
            if (!signature.Namespace.AsSpan().SequenceEqual("git"u8))
            {
                if (operation == "verify") await error.WriteLineAsync("Invalid commit signature namespace").ConfigureAwait(false);
                else if (operation == "find-principals")
                    await error.WriteLineAsync($"Error: Namespace mismatch: {PythonBytes(signature.Namespace)}").ConfigureAwait(false);
                return 1;
            }
            if (operation == "check-novalidate")
                return IsTruthy(RngitSignatures.ExtractSignedRsgData(signature.SignatureData)) ? 0 : 1;
            if (operation == "find-principals")
            {
                if (RngitSignatures.ExtractRsg(signature.SignatureData)?.GetValueOrDefault("meta")
                    is not Dictionary<object, object?> meta || meta.GetValueOrDefault("signer") is not byte[] signer)
                {
                    await error.WriteLineAsync("Could not determine signer identity: missing or invalid signer metadata").ConfigureAwait(false);
                    return 1;
                }
                await output.WriteLineAsync(Convert.ToHexStringLower(signer)).ConfigureAwait(false);
                return 0;
            }
            var validated = RngitSignatures.ValidateRsg(signature.SignatureData, message);
            if (!validated.IsValid)
            {
                await error.WriteLineAsync("Invalid signature").ConfigureAwait(false);
                return 1;
            }
            var author = ExtractAuthor(message);
            var signerHash = Convert.ToHexStringLower(validated.SignerHash!);
            if (author != signerHash)
            {
                await output.WriteLineAsync($"Commit not signed by author <{author}>").ConfigureAwait(false);
                return 1;
            }
            if (options.GetValueOrDefault("-I") is { Length: > 0 } principal && principal != signerHash)
            {
                await error.WriteLineAsync("Principal mismatch").ConfigureAwait(false);
                return 1;
            }
            await output.WriteLineAsync($"Good \"git\" signature for commit, signed with Reticulum Identity key <{signerHash}>").ConfigureAwait(false);
            return 0;
        }
        catch (ParsingException ex)
        {
            await error.WriteAsync(Usage + $"rngcs: error: {ex.Message}\n").ConfigureAwait(false);
            return 2;
        }
        catch (UnknownArgumentException ex)
        {
            await error.WriteLineAsync($"Error: Unknown argument: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await error.WriteLineAsync("rngcs: Operation cancelled.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or FormatException or InvalidOperationException)
        {
            if (operation != "check-novalidate") await error.WriteLineAsync($"rngcs: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> SignAsync(Dictionary<string, string?> options, Stream input, TextWriter output,
        TextWriter error, CancellationToken stop)
    {
        var keyPath = options.GetValueOrDefault("-f");
        if (keyPath is null || !File.Exists(keyPath))
        {
            await error.WriteLineAsync($"Identity file not found: {keyPath ?? "None"}").ConfigureAwait(false);
            return 1;
        }
        Identity identity;
        byte[]? key = null;
        try
        {
            await using var keyFile = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (keyFile.Length != Identity.KeySize) throw new ArgumentException("Identity private key must be 64 bytes.");
            key = new byte[Identity.KeySize];
            await keyFile.ReadExactlyAsync(key, stop).ConfigureAwait(false);
            identity = Identity.FromPrivateKey(key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await error.WriteLineAsync("Error: Could not load identity or identity has no private key").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
        using (identity)
        {
            var file = options.GetValueOrDefault("file");
            var useFile = file is not null && File.Exists(file);
            var message = useFile
                ? await ReadFileAsync(file!, RngitSignatures.MaximumMessageBytes, stop).ConfigureAwait(false)
                : await ReadBoundedAsync(input, RngitSignatures.MaximumMessageBytes, stop).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            byte[] rsg;
            try { rsg = RngitSignatures.CreateRsg(identity, message); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                await error.WriteLineAsync($"Error creating signature: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
            var armor = RngitSignatures.ArmorSshSignature(RngitSignatures.CreateSshSignature(identity, rsg));
            stop.ThrowIfCancellationRequested();
            if (useFile)
            {
                try { await File.WriteAllTextAsync(file + ".sig", armor, new UTF8Encoding(false), stop).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await error.WriteLineAsync($"Error writing signature file: {ex.Message}").ConfigureAwait(false);
                    return 1;
                }
            }
            else await output.WriteAsync(armor.AsMemory(), stop).ConfigureAwait(false);
            return 0;
        }
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        if (args.Length > 1024 || args.Any(a => a.Length > 32768)) throw new ArgumentException("Too many or oversized arguments.");
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var unknown = new List<string>();
        var positional = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!positional && arg == "--") { positional = true; continue; }
            if (!positional && arg is "-h" or "--help") { result["--help"] = null; return result; }
            if (!positional && arg.Length >= 2 && arg[..2] is "-Y" or "-n" or "-f" or "-I" or "-s" or "-O")
            {
                var option = arg[..2];
                if (arg.Length > 2) result[option] = arg[2] == '=' ? arg[3..] : arg[2..];
                else if (++index < args.Length && (!args[index].StartsWith('-') || IsNegativeNumber(args[index]))) result[option] = args[index];
                else throw new ParsingException($"argument {option}: expected one argument");
                if (option == "-Y" && result[option] is not ("sign" or "find-principals" or "check-novalidate" or "verify"))
                    throw new ParsingException($"argument -Y: invalid choice: '{result[option]}' (choose from sign, find-principals, check-novalidate, verify)");
            }
            else if (!positional && arg.StartsWith('-'))
                unknown.Add(arg);
            else if (!result.TryAdd("file", arg)) unknown.Add(arg);
        }
        if (!result.ContainsKey("-Y")) throw new ParsingException("the following arguments are required: -Y");
        if (unknown.Count != 0) throw new UnknownArgumentException(unknown[0]);
        return result;
    }

    private static bool IsNegativeNumber(string arg) => arg.Length > 1 && arg[0] == '-'
        && arg[1..].Count(c => c == '.') <= 1 && arg[1..].Any(char.IsAsciiDigit)
        && arg[1..].All(c => char.IsAsciiDigit(c) || c == '.')
        && !arg.EndsWith('.');

    private sealed class ParsingException(string message) : Exception(message);
    private sealed class UnknownArgumentException(string message) : Exception(message);

    private static async Task<byte[]> ReadFileAsync(string path, int limit, CancellationToken stop)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await ReadBoundedAsync(stream, limit, stop).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken stop)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, limit + 1 - (int)buffer.Length)), stop).ConfigureAwait(false);
            if (count == 0) return buffer.ToArray();
            buffer.Write(chunk, 0, count);
            if (buffer.Length > limit) throw new IOException($"Input exceeds the {limit}-byte limit.");
        }
    }

    private static string ExtractAuthor(byte[] message)
    {
        // Python bytes.splitlines splits CR, LF and CRLF, unlike text splitlines.
        var lines = Encoding.Latin1.GetString(message).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        string? author = null;
        string? tagger = null;
        var isTag = false;
        foreach (var line in lines)
        {
            if (line.Length == 0) break;
            if (line.StartsWith("tag ", StringComparison.Ordinal)) isTag = true;
            if (line.StartsWith("author ", StringComparison.Ordinal) && author is null) author = Email(line, 7);
            if (line.StartsWith("tagger ", StringComparison.Ordinal) && isTag && tagger is null) tagger = Email(line, 7);
        }
        return (isTag ? tagger : author) ?? "";
    }

    private static string? Email(string line, int prefixLength)
    {
        var start = line.IndexOf('<');
        var end = line.IndexOf('>');
        if (start <= prefixLength || end <= start || end >= line.Length - 1) return null;
        try { return new UTF8Encoding(false, true).GetString(Encoding.Latin1.GetBytes(line[(start + 1)..end])); }
        catch (DecoderFallbackException) { return ""; }
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        long n => n != 0,
        ulong n => n != 0,
        double n => n != 0,
        string s => s.Length != 0,
        byte[] b => b.Length != 0,
        object?[] a => a.Length != 0,
        Dictionary<object, object?> d => d.Count != 0,
        _ => true
    };

    private static string PythonBytes(byte[] bytes)
    {
        var quote = bytes.Contains((byte)'\'') && !bytes.Contains((byte)'"') ? '"' : '\'';
        var result = new StringBuilder("b").Append(quote);
        foreach (var b in bytes)
        {
            if (b is >= 32 and <= 126 && b != quote && b != '\\') result.Append((char)b);
            else if (b == quote || b == '\\') result.Append('\\').Append((char)b);
            else if (b == '\t') result.Append("\\t");
            else if (b == '\r') result.Append("\\r");
            else if (b == '\n') result.Append("\\n");
            else result.Append("\\x").Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.Append(quote).ToString();
    }
}
