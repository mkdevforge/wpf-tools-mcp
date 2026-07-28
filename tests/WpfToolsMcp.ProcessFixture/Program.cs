using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length > 0 && string.Equals(args[0], "crash", StringComparison.Ordinal))
{
    if (!FixtureProgram.IsCrashAuthorized())
    {
        return FixtureProgram.RefuseCrash();
    }

    FixtureProgram.Crash();
}

return await FixtureProgram.RunAsync(args);

internal static class FixtureProgram
{
    private const string CrashOptInVariable = "WPF_TOOLS_MCP_RUN_UNHANDLED_CRASH_FIXTURE";

    public static bool IsCrashAuthorized() =>
        string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"),
            "github-hosted",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Environment.GetEnvironmentVariable(CrashOptInVariable), "1", StringComparison.Ordinal);

    public static int RefuseCrash()
    {
        Console.Error.WriteLine(
            $"Crash mode refused. It requires a GitHub-hosted Actions runner and {CrashOptInVariable}=1.");
        return 64;
    }

    [DoesNotReturn]
    public static void Crash()
    {
        var errorMode = OperatingSystem.IsWindows() ? GetErrorMode() : 0;
        Console.Error.WriteLine(
            $"fixture-unhandled-crash-marker pid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} " +
            $"error-mode=0x{errorMode.ToString("X8", CultureInfo.InvariantCulture)}");
        Console.Error.Flush();
        throw new InvalidOperationException("Fixture unhandled crash.");
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Fail("A fixture command is required.");
        }

        try
        {
            return args[0] switch
            {
                "report" => Report(args),
                "emit" => Emit(args),
                "hang" => await HangAsync(args),
                "spawn-tree" => await SpawnTreeAsync(args),
                _ => Fail($"Unknown fixture command '{args[0]}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static int Report(IReadOnlyList<string> args)
    {
        var markerPath = Path.GetFullPath(RequiredOption(args, "--marker-path"));
        WriteFile(markerPath, "fixture-marker");

        var report = new
        {
            ProcessId = Environment.ProcessId,
            UserProfile = Environment.GetEnvironmentVariable("USERPROFILE"),
            AppData = Environment.GetEnvironmentVariable("APPDATA"),
            LocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Temp = Environment.GetEnvironmentVariable("TEMP"),
            Tmp = Environment.GetEnvironmentVariable("TMP"),
            UserProfileFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ApplicationDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LocalApplicationDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ErrorMode = OperatingSystem.IsWindows() ? GetErrorMode() : 0
        };
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }

    private static int Emit(IReadOnlyList<string> args)
    {
        var exitCode = int.Parse(
            RequiredOption(args, "--exit-code"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture);
        var blocks = int.Parse(
            RequiredOption(args, "--blocks"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture);
        var payload = new string('x', 1024);

        for (var index = 0; index < blocks; index++)
        {
            Console.Out.WriteLine($"stdout-{index.ToString("D4", CultureInfo.InvariantCulture)}-{payload}");
            Console.Error.WriteLine($"stderr-{index.ToString("D4", CultureInfo.InvariantCulture)}-{payload}");
        }

        Console.Out.WriteLine("stdout-final-diagnostic-marker");
        Console.Error.WriteLine("stderr-final-diagnostic-marker");

        return exitCode;
    }

    private static async Task<int> HangAsync(IReadOnlyList<string> args)
    {
        var pidPath = Path.GetFullPath(RequiredOption(args, "--pid-path"));
        WriteFile(pidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine($"hanging pid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> SpawnTreeAsync(IReadOnlyList<string> args)
    {
        var rootPidPath = Path.GetFullPath(RequiredOption(args, "--root-pid-path"));
        var childPidPath = Path.GetFullPath(RequiredOption(args, "--child-pid-path"));
        WriteFile(rootPidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve the fixture executable path.");
        var childStartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        childStartInfo.ArgumentList.Add("hang");
        childStartInfo.ArgumentList.Add("--pid-path");
        childStartInfo.ArgumentList.Add(childPidPath);

        using var child = Process.Start(childStartInfo)
            ?? throw new InvalidOperationException("Failed to start the fixture child process.");
        WriteFile(childPidPath, child.Id.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(
            $"spawned root={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} " +
            $"child={child.Id.ToString(CultureInfo.InvariantCulture)}");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static string RequiredOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 1; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        throw new ArgumentException($"Missing required option {name}.");
    }

    private static void WriteFile(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, contents);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();
}
