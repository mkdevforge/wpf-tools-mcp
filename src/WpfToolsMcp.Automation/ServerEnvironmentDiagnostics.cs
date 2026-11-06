using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

/// <summary>
/// Describes the machine and session the server runs in, so an agent driving a remote or virtual
/// machine can see up front whether physical input and capture will work and how it will render.
/// </summary>
public static class ServerEnvironmentDiagnostics
{
    private const int MaxRegistryValueLength = 256;
    private static readonly TimeSpan RenderTierTimeout = TimeSpan.FromSeconds(10);

    public static async Task<GetServerEnvironmentResponse> CollectAsync(CancellationToken cancellationToken = default)
    {
        var rendering = await GetRenderingAsync(cancellationToken).ConfigureAwait(false);
        var displays = DisplayDiagnostics.ListDisplays();

        return new GetServerEnvironmentResponse(
            Server: new ServerProcessEnvironment(
                Environment.ProcessId,
                RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                RuntimeInformation.FrameworkDescription),
            OperatingSystem: GetOperatingSystem(),
            Hardware: GetHardware(),
            InteractiveDesktop: InteractiveDesktop.GetStatus(),
            Displays: new DisplayEnvironment(
                displays.VirtualScreen,
                displays.Displays,
                GetDisplayAdapters(displays.Displays)),
            Rendering: rendering);
    }

    private static OperatingSystemEnvironment GetOperatingSystem()
    {
        const string currentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var build = ReadLocalMachineString(currentVersionKey, "CurrentBuildNumber");
        var ubr = ReadLocalMachineDword(currentVersionKey, "UBR");

        // ProductName is deliberately not reported: Windows 11 still stores "Windows 10 ..." there.
        return new OperatingSystemEnvironment(
            RuntimeInformation.OSDescription,
            EditionId: ReadLocalMachineString(currentVersionKey, "EditionID"),
            DisplayVersion: ReadLocalMachineString(currentVersionKey, "DisplayVersion"),
            Build: build is null ? null : ubr is null ? build : $"{build}.{ubr}");
    }

    private static HardwareEnvironment GetHardware()
    {
        const string biosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
        return new HardwareEnvironment(
            SystemManufacturer: ReadLocalMachineString(biosKey, "SystemManufacturer"),
            SystemProductName: ReadLocalMachineString(biosKey, "SystemProductName"));
    }

    private static IReadOnlyList<DisplayAdapterInfo> GetDisplayAdapters(IReadOnlyList<DisplayInfo> displays)
    {
        var wanted = new HashSet<string>(
            displays.Select(display => display.DeviceName).Where(name => name.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var adapters = new List<DisplayAdapterInfo>();
        if (wanted.Count == 0)
        {
            return adapters;
        }

        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint index = 0; index < 64 && EnumDisplayDevices(null, index, ref device, 0); index++)
        {
            var deviceName = device.DeviceName?.TrimEnd('\0') ?? string.Empty;
            var adapterName = device.DeviceString?.TrimEnd('\0') ?? string.Empty;
            if (wanted.Contains(deviceName) && adapterName.Length > 0)
            {
                adapters.Add(new DisplayAdapterInfo(deviceName, adapterName));
            }

            device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }

        return adapters;
    }

    private static Task<RenderingEnvironment> GetRenderingAsync(CancellationToken cancellationToken)
    {
        // RenderCapability needs a WPF dispatcher thread; use a short-lived STA thread so the
        // server's own threads never acquire a dispatcher.
        var completion = new TaskCompletionSource<RenderingEnvironment>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(new RenderingEnvironment(WpfRenderTier: RenderCapability.Tier >> 16));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"server_environment: WPF render tier unavailable: {ex.GetType().Name}: {ex.Message}");
                completion.TrySetResult(new RenderingEnvironment(UnavailableReason: "render_tier_query_failed"));
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "WpfToolsMcp render tier probe"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return WaitForRenderingAsync(completion.Task, cancellationToken);
    }

    private static async Task<RenderingEnvironment> WaitForRenderingAsync(
        Task<RenderingEnvironment> probe,
        CancellationToken cancellationToken)
    {
        var finished = await Task.WhenAny(probe, Task.Delay(RenderTierTimeout, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (finished == probe)
        {
            return await probe.ConfigureAwait(false);
        }

        Trace.TraceWarning("server_environment: WPF render tier probe timed out.");
        return new RenderingEnvironment(UnavailableReason: "render_tier_query_timeout");
    }

    private static string? ReadLocalMachineString(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) is not string value)
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            return trimmed.Length <= MaxRegistryValueLength ? trimmed : trimmed[..MaxRegistryValueLength];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Trace.TraceWarning($"server_environment: cannot read HKLM\\{keyPath}\\{valueName}: {ex.Message}");
            return null;
        }
    }

    private static int? ReadLocalMachineDword(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Trace.TraceWarning($"server_environment: cannot read HKLM\\{keyPath}\\{valueName}: {ex.Message}");
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceIndex, ref DISPLAY_DEVICE displayDevice, uint flags);
}
