using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfToolsMcp.Automation;

internal static class InjectorProcessRunner
{
    internal const string TimeoutEnvironmentVariable = "WPF_TOOLS_MCP_INJECTOR_TIMEOUT_MS";
    internal const int DefaultTimeoutMs = 15_000;
    internal const int MinimumTimeoutMs = 1_000;
    internal const int MaximumTimeoutMs = 120_000;
    internal const uint SuppressedErrorModeFlags =
        WindowsErrorMode.SemFailCriticalErrors |
        WindowsErrorMode.SemNoGpFaultErrorBox |
        WindowsErrorMode.SemNoOpenFileErrorBox;

    private const int MaximumCapturedCharactersPerStream = 16 * 1024;
    private static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OutputDrainWait = TimeSpan.FromSeconds(2);
    private static readonly object ProcessStartErrorModeLock = new();

    public static TimeSpan GetConfiguredTimeout()
    {
        string? rawValue;
        try
        {
            rawValue = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
        }
        catch
        {
            rawValue = null;
        }

        return TimeSpan.FromMilliseconds(ParseTimeoutMilliseconds(rawValue));
    }

    internal static int ParseTimeoutMilliseconds(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue) ||
            !int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
        {
            return DefaultTimeoutMs;
        }

        return Math.Clamp(parsed, MinimumTimeoutMs, MaximumTimeoutMs);
    }

    public static async Task<InjectionRunResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The launcher timeout must be positive.");
        }

        if (startInfo.UseShellExecute ||
            !startInfo.RedirectStandardOutput ||
            !startInfo.RedirectStandardError)
        {
            throw new ArgumentException(
                "Injector processes require UseShellExecute=false with stdout and stderr redirected.",
                nameof(startInfo));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var process = StartProcess(startInfo);
        var processId = process.Id;
        var stopwatch = Stopwatch.StartNew();
        using var outputCancellation = new CancellationTokenSource();
        var stdout = new BoundedTextCapture(MaximumCapturedCharactersPerStream);
        var stderr = new BoundedTextCapture(MaximumCapturedCharactersPerStream);
        var outputTasks = Task.CompletedTask;
        Task? exitTask = null;
        var outputCaptureStarted = false;

        try
        {
            var stdoutTask = stdout.DrainAsync(process.StandardOutput, outputCancellation.Token);
            var stderrTask = stderr.DrainAsync(process.StandardError, outputCancellation.Token);
            outputTasks = Task.WhenAll(stdoutTask, stderrTask);
            outputCaptureStarted = true;
            exitTask = process.WaitForExitAsync(CancellationToken.None);

            try
            {
                await exitTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (TryHasExited(process))
                {
                    return await CompleteNaturalExitAsync(
                        process,
                        outputTasks,
                        outputCancellation,
                        stdout,
                        stderr,
                        startInfo.FileName,
                        processId,
                        stopwatch.Elapsed).ConfigureAwait(false);
                }

                var termination = await TerminateProcessTreeAsync(process, exitTask).ConfigureAwait(false);
                await StopOutputCaptureAsync(
                    process,
                    outputTasks,
                    outputCancellation).ConfigureAwait(false);

                if (termination.ExitedBeforeKill)
                {
                    return CreateResult(
                        process,
                        stdout,
                        stderr,
                        startInfo.FileName,
                        processId,
                        stopwatch.Elapsed);
                }

                throw new OperationCanceledException(
                    BuildInterruptedMessage(
                        interruption: "was canceled",
                        startInfo.FileName,
                        processId,
                        stopwatch.Elapsed,
                        termination,
                        stdout.Snapshot(),
                        stderr.Snapshot()),
                    innerException: null,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                if (TryHasExited(process))
                {
                    return await CompleteNaturalExitAsync(
                        process,
                        outputTasks,
                        outputCancellation,
                        stdout,
                        stderr,
                        startInfo.FileName,
                        processId,
                        stopwatch.Elapsed).ConfigureAwait(false);
                }

                var termination = await TerminateProcessTreeAsync(process, exitTask).ConfigureAwait(false);
                await StopOutputCaptureAsync(
                    process,
                    outputTasks,
                    outputCancellation).ConfigureAwait(false);

                if (termination.ExitedBeforeKill)
                {
                    return CreateResult(
                        process,
                        stdout,
                        stderr,
                        startInfo.FileName,
                        processId,
                        stopwatch.Elapsed);
                }

                throw new TimeoutException(
                    BuildInterruptedMessage(
                        interruption: $"timed out after {timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms",
                        startInfo.FileName,
                        processId,
                        stopwatch.Elapsed,
                        termination,
                        stdout.Snapshot(),
                        stderr.Snapshot()));
            }

            return await CompleteNaturalExitAsync(
                process,
                outputTasks,
                outputCancellation,
                stdout,
                stderr,
                startInfo.FileName,
                processId,
                stopwatch.Elapsed).ConfigureAwait(false);
        }
        finally
        {
            if (!TryHasExited(process))
            {
                exitTask ??= process.WaitForExitAsync(CancellationToken.None);
                _ = await TerminateProcessTreeAsync(process, exitTask).ConfigureAwait(false);
            }

            if (outputCaptureStarted)
            {
                await StopOutputCaptureAsync(
                    process,
                    outputTasks,
                    outputCancellation).ConfigureAwait(false);
            }
        }
    }

    internal static uint GetCurrentErrorMode() => WindowsErrorMode.GetCurrent();

    private static Process StartProcess(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };

        try
        {
            bool started;
            lock (ProcessStartErrorModeLock)
            {
                if (OperatingSystem.IsWindows())
                {
                    var previousMode = WindowsErrorMode.GetCurrent();
                    WindowsErrorMode.Set(previousMode | SuppressedErrorModeFlags);
                    try
                    {
                        started = process.Start();
                    }
                    finally
                    {
                        WindowsErrorMode.Set(previousMode);
                    }
                }
                else
                {
                    started = process.Start();
                }
            }

            if (!started)
            {
                throw new InvalidOperationException("Process.Start returned false.");
            }

            return process;
        }
        catch (Exception ex)
        {
            process.Dispose();
            var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                ? Environment.CurrentDirectory
                : startInfo.WorkingDirectory;
            throw new InvalidOperationException(
                $"Failed to start injector launcher '{startInfo.FileName}' in '{workingDirectory}'. " +
                $"{ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    private static async Task<InjectionRunResult> CompleteNaturalExitAsync(
        Process process,
        Task outputTasks,
        CancellationTokenSource outputCancellation,
        BoundedTextCapture stdout,
        BoundedTextCapture stderr,
        string executablePath,
        int processId,
        TimeSpan duration)
    {
        await StopOutputCaptureAsync(
            process,
            outputTasks,
            outputCancellation,
            closeImmediately: false).ConfigureAwait(false);
        return CreateResult(
            process,
            stdout,
            stderr,
            executablePath,
            processId,
            duration);
    }

    private static InjectionRunResult CreateResult(
        Process process,
        BoundedTextCapture stdout,
        BoundedTextCapture stderr,
        string executablePath,
        int processId,
        TimeSpan duration) =>
        new(process.ExitCode, stdout.Snapshot(), stderr.Snapshot())
        {
            ExecutablePath = executablePath,
            ProcessId = processId,
            Duration = duration
        };

    private static async Task<ProcessTerminationResult> TerminateProcessTreeAsync(
        Process process,
        Task exitTask)
    {
        if (TryHasExited(process))
        {
            return new ProcessTerminationResult(
                KillRequested: false,
                ProcessExited: true,
                ExitedBeforeKill: true,
                Detail: "process exited before termination was requested");
        }

        var killRequested = false;
        string? killError = null;
        try
        {
            process.Kill(entireProcessTree: true);
            killRequested = true;
        }
        catch (InvalidOperationException) when (TryHasExited(process))
        {
            return new ProcessTerminationResult(
                KillRequested: false,
                ProcessExited: true,
                ExitedBeforeKill: true,
                Detail: "process exited before termination was requested");
        }
        catch (Exception ex)
        {
            killError = $"{ex.GetType().Name}: {ex.Message}";
        }

        var exited = TryHasExited(process);
        if (!exited)
        {
            try
            {
                await exitTask.WaitAsync(TerminationWait, CancellationToken.None).ConfigureAwait(false);
                exited = true;
            }
            catch (TimeoutException)
            {
                exited = TryHasExited(process);
            }
            catch
            {
                exited = TryHasExited(process);
            }
        }

        var detail = killError is not null
            ? $"tree kill failed ({killError}); root exited={exited.ToString().ToLowerInvariant()}"
            : $"tree kill requested={killRequested.ToString().ToLowerInvariant()}; root exited={exited.ToString().ToLowerInvariant()}";
        return new ProcessTerminationResult(
            killRequested,
            exited,
            ExitedBeforeKill: false,
            detail);
    }

    private static async Task StopOutputCaptureAsync(
        Process process,
        Task outputTasks,
        CancellationTokenSource outputCancellation,
        bool closeImmediately = true)
    {
        if (!closeImmediately)
        {
            try
            {
                await outputTasks.WaitAsync(OutputDrainWait, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch
            {
                // A descendant can retain inherited pipes; stop waiting after the bounded drain.
            }
        }

        try
        {
            outputCancellation.Cancel();
        }
        catch
        {
        }

        TryClose(process.StandardOutput);
        TryClose(process.StandardError);

        try
        {
            await outputTasks.WaitAsync(OutputDrainWait, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            ObserveFault(outputTasks);
        }
    }

    private static bool TryHasExited(Process process)
    {
        try
        {
            process.Refresh();
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryClose(StreamReader reader)
    {
        try
        {
            reader.Close();
        }
        catch
        {
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string BuildInterruptedMessage(
        string interruption,
        string executablePath,
        int processId,
        TimeSpan elapsed,
        ProcessTerminationResult termination,
        string stdout,
        string stderr)
    {
        var builder = new StringBuilder();
        builder.Append("Injector launcher '")
            .Append(executablePath)
            .Append("' (PID ")
            .Append(processId.ToString(CultureInfo.InvariantCulture))
            .Append(") ")
            .Append(interruption)
            .Append("; elapsed=")
            .Append(elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture))
            .Append(" ms; termination=")
            .Append(termination.Detail)
            .Append('.');
        AppendOutput(builder, "stdout", stdout);
        AppendOutput(builder, "stderr", stderr);
        return builder.ToString();
    }

    private static void AppendOutput(StringBuilder builder, string name, string value)
    {
        builder.AppendLine()
            .Append("--- ")
            .Append(name)
            .AppendLine(" ---")
            .Append(string.IsNullOrWhiteSpace(value) ? "<empty>" : value.TrimEnd());
    }

    private sealed record ProcessTerminationResult(
        bool KillRequested,
        bool ProcessExited,
        bool ExitedBeforeKill,
        string Detail);

    private sealed class BoundedTextCapture
    {
        private readonly object _sync = new();
        private readonly StringBuilder _head;
        private readonly StringBuilder _tail;
        private readonly int _maximumCharacters;
        private readonly int _headCharacters;
        private readonly int _tailCharacters;
        private long _totalCharacters;

        public BoundedTextCapture(int maximumCharacters)
        {
            _maximumCharacters = maximumCharacters;
            _headCharacters = maximumCharacters / 2;
            _tailCharacters = maximumCharacters - _headCharacters;
            _head = new StringBuilder(_headCharacters);
            _tail = new StringBuilder(_tailCharacters);
        }

        public async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    lock (_sync)
                    {
                        _totalCharacters += read;
                        var offset = 0;
                        var headRemaining = _headCharacters - _head.Length;
                        if (headRemaining > 0)
                        {
                            var headRead = Math.Min(headRemaining, read);
                            _head.Append(buffer, 0, headRead);
                            offset = headRead;
                        }

                        if (offset < read)
                        {
                            _tail.Append(buffer, offset, read - offset);
                            if (_tail.Length > _tailCharacters)
                            {
                                _tail.Remove(0, _tail.Length - _tailCharacters);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        }

        public string Snapshot()
        {
            lock (_sync)
            {
                if (_totalCharacters <= _maximumCharacters)
                {
                    return _head.ToString() + _tail;
                }

                return _head + Environment.NewLine +
                       $"...[output truncated; observed {_totalCharacters.ToString(CultureInfo.InvariantCulture)} characters; " +
                       $"showing first {_headCharacters.ToString(CultureInfo.InvariantCulture)} and " +
                       $"last {_tailCharacters.ToString(CultureInfo.InvariantCulture)}]..." +
                       Environment.NewLine + _tail;
            }
        }
    }

    private static class WindowsErrorMode
    {
        public const uint SemFailCriticalErrors = 0x0001;
        public const uint SemNoGpFaultErrorBox = 0x0002;
        public const uint SemNoOpenFileErrorBox = 0x8000;

        public static uint GetCurrent() => OperatingSystem.IsWindows() ? GetErrorMode() : 0;

        public static void Set(uint mode)
        {
            if (OperatingSystem.IsWindows())
            {
                _ = SetErrorMode(mode);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetErrorMode();

        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint uMode);
    }
}
