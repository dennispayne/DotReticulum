// Read-only hosted-source check; no Git object fetch or Python networking.
// dotnet run --file tests\Interop\CheckNomadCitation.cs -- <owned-state-directory> <pinned-checkout>
#:project ..\..\src\DotReticulum.Applications\DotReticulum.Applications.csproj
#:property LangVersion=13.0
#:property Nullable=enable
#:property ImplicitUsings=enable

using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications;
using DotReticulum.Applications.Rngit;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Core;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: CheckNomadCitation.cs <new-owned-state-directory> <pinned-checkout>");
    return 2;
}

const string revision = "a29bfb51c468945c96a1ee22cae0114e993e2391";
const string gitDestination = "7649a50d84610232d1416b41d2896aff";
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new IOException("Refusing to reuse an existing state directory.");
Directory.CreateDirectory(root);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
try
{
    await using var node = ReticulumNode.Create(RnsdConfigParser.Parse("""
        [reticulum]
          share_instance = no
          enable_transport = no
        [interfaces]
          [[Public citation check]]
            type = TCPClientInterface
            enabled = yes
            target_host = reticulum.thecryptosquid.com
            target_port = 4242
        """), root);
    await node.StartAsync(deadline.Token);
    using var remote = await node.ResolveIdentityAsync(Convert.FromHexString(gitDestination),
        TimeSpan.FromSeconds(45), deadline.Token)
        ?? throw new IOException("Repositories destination path/identity was not resolved.");
    var pageDestination = Convert.ToHexStringLower(
        Destination.ComputeHashFromNameAndIdentity("nomadnetwork.node", remote.Hash));
    Console.WriteLine($"gitDestination={gitDestination}\npageDestination={pageDestination}");
    using var caller = Identity.Generate();
    using var pages = await RngitManagementClient.ConnectAsync(node, caller,
        Convert.FromHexString(pageDestination), TimeSpan.FromSeconds(45), deadline.Token);
    var data = new Dictionary<object, object?>
    {
        ["var_g"] = "reticulum", ["var_r"] = "reticulum", ["var_ref"] = revision,
        ["var_path"] = "README.md", ["var_raw"] = "y",
    };
    var page = Encoding.UTF8.GetString((await pages.RequestAsync("/page/blob.mu", data, deadline.Token)).Data);
    var uri = pageDestination + ":/page/blob.mu`g=reticulum|r=reticulum|ref=" + revision + "|path=README.md|raw=y";
    if (!page.Contains(revision, StringComparison.Ordinal) || !page.Contains("README.md", StringComparison.Ordinal)
        || !page.Contains("All development is happening elsewhere.", StringComparison.Ordinal))
        throw new InvalidDataException("Blob page did not identify the pinned file, revision and expected passage.");
    var downloaded = (await pages.RequestAsync("/file/download", data, deadline.Token)).Data;
    var expected = await File.ReadAllBytesAsync(Path.Combine(args[1], "README.md"), deadline.Token);
    if (!downloaded.AsSpan().SequenceEqual(expected))
        throw new InvalidDataException("Downloaded pinned README differs from the local pinned checkout.");
    Console.WriteLine($"uri={uri}\nrevision={revision}\nfile=README.md");
    Console.WriteLine($"pageBytes={Encoding.UTF8.GetByteCount(page)}\ndownloadedBytes={downloaded.Length}");
    Console.WriteLine($"sha256={Convert.ToHexStringLower(SHA256.HashData(downloaded))}");
    Console.WriteLine("identicalToLocalPinnedFile=true");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
    return 1;
}
