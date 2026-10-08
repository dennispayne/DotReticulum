using System.Security.Cryptography;
using DotReticulum.Core;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        DotReticulum — experimental Reticulum primitives

        rnid --generate <file>   Create a raw 64-byte private identity (never overwrite).
        rnid --show <file>       Show the identity hash and public key only.

        Networking, routing, links, resources and LXMF are not implemented yet.
        """);
    return 0;
}

if (args.Length != 3 || args[0] != "rnid" || args[1] is not ("--generate" or "--show"))
{
    Console.Error.WriteLine("Invalid arguments. Use --help.");
    return 2;
}

try
{
    using var identity = args[1] == "--generate"
        ? Identity.Generate()
        : LoadIdentity(args[2]);

    if (args[1] == "--generate")
    {
        var key = identity.ExportPrivateKey();
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var file = new FileStream(args[2], options);
            file.Write(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    Console.WriteLine($"Identity: {Convert.ToHexString(identity.Hash).ToLowerInvariant()}");
    Console.WriteLine($"Public key: {Convert.ToHexString(identity.ExportPublicKey()).ToLowerInvariant()}");
    if (args[1] == "--generate")
    {
        Console.WriteLine("Protect this private identity file; on Windows verify its ACL before use.");
    }

    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
    or ArgumentException or CryptographicException)
{
    Console.Error.WriteLine($"Identity operation failed: {exception.Message}");
    return 1;
}

static Identity LoadIdentity(string path)
{
    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (file.Length != 64)
    {
        throw new ArgumentException("An identity file must contain exactly 64 raw private-key bytes.");
    }

    var key = new byte[64];
    try
    {
        file.ReadExactly(key);
        return Identity.FromPrivateKey(key);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
    }
}
