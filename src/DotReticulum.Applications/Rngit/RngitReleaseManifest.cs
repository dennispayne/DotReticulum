using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Applications.Rngit;

public sealed record RngitReleaseArtifact(string Name, byte[] Signature);
public sealed record RngitReleaseManifest(string Name, string Version, byte[] Origin, string Repository,
    byte[] Notes, byte[] SignerHash, IReadOnlyList<RngitReleaseArtifact> Artifacts);
public sealed record RngitReleaseArtifactValidation(string Name, bool IsValid, string? Error);
public sealed record RngitReleaseVerification(RngitReleaseManifest Manifest, IReadOnlyList<RngitReleaseArtifactValidation> Artifacts)
{
    public bool IsValid => Artifacts.Count > 0 && Artifacts.All(artifact => artifact.IsValid);
}

/// <summary>
/// rnid.py:588-601 structural checks and rngit/server.py:878-1148 release envelopes.
/// A valid embedded key proves a signature, not publisher trust; requiredSigner must be independent.
/// Safe names, bounded artifact lists and duplicate rejection deliberately harden upstream basename handling.
/// </summary>
public static class RngitReleaseManifests
{
    public static RngitReleaseManifest Validate(byte[] bytes, byte[]? requiredSigner = null)
    {
        var envelope = RngitSignatures.ExtractRsg(bytes);
        if (envelope?.GetValueOrDefault("message") is not byte[] notes || notes.Length == 0)
            throw new InvalidDataException("No embedded message in release manifest");
        var validation = RngitSignatures.ValidateRsg(bytes, notes, requiredSigner ?? []);
        if (!validation.IsValid) throw new InvalidDataException("Could not validate release manifest signature or required signer");
        var data = validation.SignedData!;
        if (data.GetValueOrDefault("meta") is not Dictionary<object, object?> meta)
            throw new InvalidDataException("No release metadata in manifest");
        if (meta.GetValueOrDefault("name") is not string name || string.IsNullOrEmpty(name)
            || meta.GetValueOrDefault("version") is not string version || string.IsNullOrEmpty(version))
            throw new InvalidDataException("Incomplete package data in manifest");
        if (!RngitReleaseService.ValidName(name) || !RngitReleaseService.ValidName(version))
            throw new InvalidDataException("Invalid data in release manifest");
        if (meta.GetValueOrDefault("origin") is not byte[] origin || origin.Length != 16
            || meta.GetValueOrDefault("path") is not string repository)
            throw new InvalidDataException("Incomplete or invalid release origin data in manifest");
        try { RngitAddress.Parse($"rns://{Convert.ToHexString(origin)}/{repository}"); }
        catch (ArgumentException e) { throw new InvalidDataException("Invalid release origin path in manifest", e); }
        if (meta.GetValueOrDefault("artifacts") is not object[] entries || entries.Length is 0 or > RngitReleaseService.MaximumArtifacts)
            throw new InvalidDataException("Release manifest contains no artifacts or exceeds the artifact limit");
        var artifacts = new List<RngitReleaseArtifact>();
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is not Dictionary<object, object?> artifact || artifact.GetValueOrDefault("name") is not string artifactName
                || !RngitReleaseService.ValidName(artifactName) || !names.Add(artifactName)
                || artifact.GetValueOrDefault("rsg") is not byte[] signature || signature.Length > RngitSignatures.MaximumSignatureBytes)
                throw new InvalidDataException("Invalid or duplicate artifact in release manifest");
            artifacts.Add(new(artifactName, signature.ToArray()));
        }
        return new(name, version, origin.ToArray(), repository, notes.ToArray(), validation.SignerHash!, artifacts.AsReadOnly());
    }

    public static byte[] Create(Identity signer, byte[] origin, string repository, string name, string version,
        string notes, string? commit, IReadOnlyDictionary<string, byte[]> artifacts, DateTimeOffset released)
    {
        if (artifacts.Count is 0 or > RngitReleaseService.MaximumArtifacts)
            throw new ArgumentException("A release must contain 1-1024 artifacts.", nameof(artifacts));
        var timestamp = released.ToUnixTimeSeconds();
        var entries = artifacts.Select(pair => new Dictionary<object, object?>
        {
            ["name"] = pair.Key,
            ["rsg"] = RngitSignatures.CreateRsgFromHash(signer, SHA256.HashData(pair.Value),
                new Dictionary<object, object?> { ["timestamp"] = timestamp })
        }).ToArray();
        var meta = new Dictionary<object, object?>
        {
            ["name"] = name, ["version"] = version, ["released"] = released.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["timestamp"] = timestamp, ["origin"] = origin, ["path"] = repository, ["commit"] = commit, ["artifacts"] = entries
        };
        var manifest = RngitSignatures.CreateRsg(signer, RngitReleaseService.EncodeNotes(notes, RngitReleaseService.MaximumNotesBytes), true, meta);
        Validate(manifest, signer.Hash.ToArray());
        return manifest;
    }
}
