using System.Diagnostics;
using System.Text;
using DotReticulum.Applications.Rngit;

if (args.Length > 0 && args[0].StartsWith("--helper-", StringComparison.Ordinal))
{
    try
    {
        switch (args)
        {
            case ["--helper-render", var directory, var name, var replacement]:
                var text = await new RngitTemplates(directory, diagnostic: Console.Error.WriteLine)
                    .RenderAsync(name, new Dictionary<string, string> { ["PAGE_CONTENT"] = replacement });
                if (text is null) return 2;
                Console.Write(text);
                return 0;
            case ["--helper-editor", var directory, var source, var executable, var script, var mode, var authorized, var milliseconds]:
                var templates = new RngitTemplates(directory,
                    timeout: TimeSpan.FromMilliseconds(int.Parse(milliseconds, System.Globalization.CultureInfo.InvariantCulture)),
                    diagnostic: Console.Error.WriteLine);
                var editor = new RngitEditor(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, "-Mode", mode, "-Path"],
                    bool.Parse(authorized));
                var edited = await templates.EditAsync(File.ReadAllText(source), editor);
                if (edited is null)
                {
                    Console.Error.WriteLine($"Owned editor fixture: executable='{executable}', script='{script}', mode='{mode}', authorized='{authorized}', directory='{directory}'.");
                    return 2;
                }
                await File.WriteAllTextAsync(source + ".edited", edited, new UTF8Encoding(false));
                Console.Write("edited");
                return 0;
            case ["--helper-media", var source, var backend, var milliseconds, var maxOutput]:
                var media = new RngitMedia(_ => backend, Console.Error.WriteLine);
                var converted = await media.ConvertAsync(await File.ReadAllBytesAsync(source), source,
                    new(Backend: "magick",
                        Timeout: TimeSpan.FromMilliseconds(int.Parse(milliseconds, System.Globalization.CultureInfo.InvariantCulture)),
                        MaxOutputBytes: int.Parse(maxOutput, System.Globalization.CultureInfo.InvariantCulture)));
                Console.Write(converted.Status);
                if (converted.Status == RngitMediaStatus.Converted)
                    await File.WriteAllBytesAsync(converted.FileName, converted.Data);
                return converted.Status == RngitMediaStatus.Failed ? 2 : 0;
            case ["--helper-native-media", var source, var backend]:
                var nativeMedia = new RngitMedia(name => name == "ffmpeg" ? backend : null, Console.Error.WriteLine);
                var nativeResult = await nativeMedia.ConvertAsync(await File.ReadAllBytesAsync(source), source,
                    new(Backend: "ffmpeg", Quality: 85, MaxDimension: 64));
                Console.Write(nativeResult.Status);
                if (nativeResult.Status == RngitMediaStatus.Converted)
                    await File.WriteAllBytesAsync(nativeResult.FileName, nativeResult.Data);
                return nativeResult.Status == RngitMediaStatus.Failed ? 2 : 0;
            default:
                Console.Error.Write("Invalid owned helper harness command.");
                return 3;
        }
    }
    catch (Exception exception)
    {
        Console.Error.Write(exception.Message);
        return 3;
    }
}

// An owned benign subprocess, not an image encoder or upstream substitute.
var root = AppContext.BaseDirectory;
var modeFile = Path.Combine(root, "rngit-backend-mode");
var backendMode = args is ["--child"] ? "timeout" : File.Exists(modeFile) ? File.ReadAllText(modeFile).Trim() : "success";
var output = Console.OpenStandardOutput();
var error = Console.OpenStandardError();
switch (backendMode)
{
    case "success":
        await Console.OpenStandardInput().CopyToAsync(Stream.Null);
        await output.WriteAsync(Convert.FromBase64String(File.ReadAllText(Path.Combine(root, "rngit-backend-output"))));
        return 0;
    case "invalid":
        await output.WriteAsync("not webp"u8.ToArray());
        return 0;
    case "fail":
        await error.WriteAsync("owned encoder failure"u8.ToArray());
        return 17;
    case "timeout":
        await Task.Delay(TimeSpan.FromSeconds(60));
        return 0;
    case "early":
        return 0;
    case "overflow":
        for (var count = 0; count < 1024; count++) await output.WriteAsync(new byte[8192]);
        return 0;
    case "template":
        await output.WriteAsync(Encoding.UTF8.GetBytes("owned π {PAGE_CONTENT}\n"));
        return 0;
    case "child":
    case "orphan-pipes":
        var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            WorkingDirectory = root,
            ArgumentList = { "--child" }
        })!;
        var pidPath = Path.Combine(root, "rngit-child-pid");
        var pendingPidPath = pidPath + ".pending";
        File.WriteAllText(pendingPidPath, process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(pendingPidPath, pidPath);
        if (backendMode == "orphan-pipes") return 0;
        await Task.Delay(TimeSpan.FromSeconds(60));
        return 0;
    default:
        return 19;
}
