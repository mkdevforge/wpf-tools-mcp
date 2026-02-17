using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WpfPilot.Automation;

internal static class ProcessArchitectureDetector
{
    // https://learn.microsoft.com/windows/win32/api/wow64apiset/nf-wow64apiset-iswow64process2
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(
        IntPtr hProcess,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    private const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0;
    private const ushort IMAGE_FILE_MACHINE_I386 = 0x014c;
    private const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    private const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

    public static Architecture GetProcessArchitecture(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture;
        }

        try
        {
            if (IsWow64Process2(process.Handle, out var processMachine, out var nativeMachine))
            {
                if (processMachine == IMAGE_FILE_MACHINE_UNKNOWN)
                {
                    return nativeMachine switch
                    {
                        IMAGE_FILE_MACHINE_AMD64 => Architecture.X64,
                        IMAGE_FILE_MACHINE_ARM64 => Architecture.Arm64,
                        _ => RuntimeInformation.ProcessArchitecture
                    };
                }

                return processMachine switch
                {
                    IMAGE_FILE_MACHINE_I386 => Architecture.X86,
                    IMAGE_FILE_MACHINE_AMD64 => Architecture.X64,
                    IMAGE_FILE_MACHINE_ARM64 => Architecture.Arm64,
                    _ => RuntimeInformation.ProcessArchitecture
                };
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows: fall back to IsWow64Process
        }

        if (!IsWow64Process(process.Handle, out var wow64))
        {
            throw new InvalidOperationException($"Failed to query process architecture. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        if (wow64)
        {
            return Architecture.X86;
        }

        return Environment.Is64BitOperatingSystem ? Architecture.X64 : Architecture.X86;
    }
}

