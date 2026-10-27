using DotReticulum.Core;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath)?.ToLowerInvariant();
if (executableName is "git-remote-rns" or "rngit" or "rngcs" or "rnid" or "rnstatus" or "rnpath" or
    "rnprobe" or "rnsh" or "rnodeconf" or "rnir" or "rnpkg" or "rncp" or "rnx" or "rnsd")
    args = [executableName, .. args];

const string Usage = """
    DotReticulum rnid - identity utility (experimental)

    Usage:
      rnid --help               Show this help
      rnid --generate <file>    Create a new identity file (never overwrites)
      rnid --show <file>        Print the identity hash and public key of a file

    Other tools:
      rnsd [--config <dir>]     Reticulum transport daemon (run "rnsd --help")
      rnstatus ...             Interface statistics and management ("rnstatus --help")
      rnpath ...               Inspect, request and manage paths ("rnpath --help")
      rnprobe ...              Probe destination reachability ("rnprobe --help")
      rnsh ...                 Authorized interactive shell ("rnsh --help")
      rnodeconf ...            RNode device maintenance ("rnodeconf --help")
      rncp ...                 File transfer ("rncp --help")
      rnx ...                  Authorized remote command execution ("rnx --help")
      rngit ...                Repository node, collaboration and bare Git transfers ("rngit --help")
      git-remote-rns ...       Native read/write Git remote helper
      rngcs ...                Git SSHSIG/RSG signing and verification ("rngcs --help")
    Identity files hold the raw 64-byte PRIVATE key. On Unix they are created
    owner-only (0600). On Windows they inherit the folder's ACLs: store them in a
    location only you can read.
    """;

if (args is ["rnid" or "rnstatus" or "rnpath" or "rnprobe" or "rnodeconf" or "rnir" or "rnpkg" or "rnsd" or "rncp" or "rnx" or "rnsh" or "rngit" or "rngcs" or "git-remote-rns", ..])
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
    return args[0] switch
    {
        "rnid" => await DotReticulum.Applications.Rnid.RnidApp.RunAsync(args[1..], Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Out, Console.Error, stop.Token),
        "rnstatus" => await DotReticulum.Applications.Rnstatus.RnstatusApp.RunAsync(args[1..], Console.Out, Console.Error, stop.Token),
        "rnpath" => await DotReticulum.Applications.Rnpath.RnpathApp.RunAsync(args[1..], Console.Out, Console.Error, stop.Token),
        "rnprobe" => await DotReticulum.Applications.Rnprobe.RnprobeApp.RunAsync(args[1..], Console.Out, Console.Error, stop.Token),
        "rnodeconf" => await DotReticulum.Applications.Rnodeconf.RnodeconfCli.RunAsync(args[1..], Console.Out, Console.Error, stop.Token),
        "rnir" or "rnpkg" => await DotReticulum.Applications.Rnodeconf.UpstreamPlaceholderCli.RunAsync(args[0], args[1..], Console.Out, Console.Error, stop.Token),
        "git-remote-rns" => await DotReticulum.Applications.Rngit.GitRemoteRnsApp.RunAsync(args[1..], new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false), Console.Out, Console.Error, stop.Token),
        "rngit" => await DotReticulum.Applications.Rngit.RngitApp.RunAsync(args[1..], Console.Out, Console.Error, stop.Token),
        "rngcs" => await DotReticulum.Applications.Rngit.RngcsApp.RunAsync(args[1..], Console.OpenStandardInput(), Console.Out, Console.Error, stop.Token),
        "rncp" => await DotReticulum.Applications.Rncp.RncpApp.RunAsync(args[1..], Console.Out, Console.Error, stop.Token),
        "rnx" => await DotReticulum.Applications.Rnx.RnxApp.RunAsync(args[1..], Console.Out, Console.Error, stop.Token, Console.OpenStandardOutput(), Console.OpenStandardError()),
        "rnsh" => await DotReticulum.Applications.Rnsh.RnshApp.RunAsync(args[1..], Console.Out, Console.Error, stop.Token),
        _ => await DotReticulum.Applications.Rnsd.RnsdApp.RunAsync(args[1..], Console.Out, Console.Error, stop.Token),
    };
}

if (args.Length == 0 || args is ["--help" or "-h" or "-?"])
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}

try
{
    switch (args)
    {
        case ["--generate", var path]:
        {
            using var identity = Identity.Generate();
            IdentityFile.SaveNew(identity, path);
            Print(identity);
            if (OperatingSystem.IsWindows()) Console.Error.WriteLine("Warning: the identity file contains your private key; restrict its Windows ACLs.");
            return 0;
        }
        case ["--show", var path]:
        {
            using var identity = IdentityFile.Load(path);
            Print(identity);
            return 0;
        }
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine($"rnid: {ex.Message}");
    return 1;
}

static void Print(Identity identity)
{
    Console.WriteLine($"Identity hash: {identity.HexHash}");
    Console.WriteLine($"Public key:    {Convert.ToHexStringLower(identity.ExportPublicKey())}");
}
