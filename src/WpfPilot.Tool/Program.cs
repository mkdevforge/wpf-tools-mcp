using System.Diagnostics;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("wpf-tools-mcp is only supported on Windows.");
    return 1;
}

var serverDir = Path.Combine(AppContext.BaseDirectory, "server");
var serverExe = Path.Combine(serverDir, "WpfPilot.McpServer.exe");
var serverDll = Path.Combine(serverDir, "WpfPilot.McpServer.dll");

if (File.Exists(serverExe))
{
    return Run(serverExe, serverDir, args);
}

if (File.Exists(serverDll))
{
    return Run("dotnet", serverDir, [serverDll, .. args]);
}

Console.Error.WriteLine($"Server payload not found under '{serverDir}'. Reinstall the tool or build from source.");
return 1;

static int Run(string fileName, string workingDirectory, string[] args)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
    };

    foreach (var arg in args)
    {
        startInfo.ArgumentList.Add(arg);
    }

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        Console.Error.WriteLine($"Failed to start '{fileName}'.");
        return 1;
    }

    process.WaitForExit();
    return process.ExitCode;
}
