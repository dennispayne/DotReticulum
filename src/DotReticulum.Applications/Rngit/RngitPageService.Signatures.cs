namespace DotReticulum.Applications.Rngit;

public sealed partial class RngitPageService
{
    private async Task<string?> CommitSignatureAsync(RngitHostedRepository repository, string hash, CancellationToken token)
    {
        var raw = await TextAsync(repository, ["cat-file", "-p", hash], token).ConfigureAwait(false);
        if (raw is null) return null;
        var signature = new List<string>();
        var unsigned = new List<string>();
        var inSignature = false;
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("gpgsig ", StringComparison.Ordinal) || line.StartsWith("gpgsig-sha256 ", StringComparison.Ordinal))
            {
                inSignature = true;
                signature.Add(line[(line.IndexOf(' ') + 1)..]);
            }
            else if (inSignature && line.StartsWith(' ')) signature.Add(line[1..]);
            else { inSignature = false; unsigned.Add(line); }
        }
        if (signature.Count == 0) return null;
        try
        {
            var wrapper = RngitSignatures.UnarmorSshSignature(string.Join('\n', signature));
            RngitSignatures.SshSignature ssh;
            try { ssh = RngitSignatures.ParseSshSignature(wrapper); }
            catch (FormatException) { return "`F900Malformed SSH wrapping for RSG data`f"; }
            var validation = RngitSignatures.ValidateRsg(ssh.SignatureData, Utf8.GetBytes(string.Join('\n', unsigned)));
            if (!validation.IsValid || validation.SignerHash is null) return "`F900Invalid signature`f";
            var signer = Convert.ToHexStringLower(validation.SignerHash);
            string? author = null;
            foreach (var line in unsigned)
            {
                if (line.Length == 0) break;
                if (!line.StartsWith("author ", StringComparison.Ordinal)) continue;
                var start = line.IndexOf('<');
                var end = line.IndexOf('>');
                if (start > 7 && end > start && end < line.Length - 1) { author = line[(start + 1)..end]; break; }
            }
            if (author is null) return "`Faa0Could not verify author`f";
            return author == signer ? "`FT66BB85Valid, signed by author`f"
                : $"`Faa0{Escape($"Invalid signer <{signer}>, author is <{author}>")}`f";
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException)
        {
            Report($"Commit signature {hash} failed: {error.Message}");
            return "`F900Signature validation error`f";
        }
    }
}
