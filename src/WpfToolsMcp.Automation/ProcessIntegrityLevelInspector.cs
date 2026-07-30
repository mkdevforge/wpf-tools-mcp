using System.Runtime.InteropServices;

namespace WpfToolsMcp.Automation;

internal enum ProcessIntegrityLevelComparison
{
    Same,
    CurrentHigher,
    TargetHigher
}

internal static class ProcessIntegrityLevelInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    internal static ProcessIntegrityLevelComparison CompareIntegrityRids(int currentRid, int targetRid)
    {
        if (currentRid < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentRid));
        }

        if (targetRid < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRid));
        }

        return currentRid.CompareTo(targetRid) switch
        {
            < 0 => ProcessIntegrityLevelComparison.TargetHigher,
            > 0 => ProcessIntegrityLevelComparison.CurrentHigher,
            _ => ProcessIntegrityLevelComparison.Same
        };
    }

    internal static bool TryCompareWithCurrentProcess(
        int targetPid,
        out ProcessIntegrityLevelComparison comparison)
    {
        comparison = default;
        if (!OperatingSystem.IsWindows() || targetPid <= 0)
        {
            return false;
        }

        var targetProcess = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, targetPid);
        if (targetProcess == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!TryReadIntegrityRid(GetCurrentProcess(), out var currentRid) ||
                !TryReadIntegrityRid(targetProcess, out var targetRid))
            {
                return false;
            }

            comparison = CompareIntegrityRids(currentRid, targetRid);
            return true;
        }
        finally
        {
            _ = CloseHandle(targetProcess);
        }
    }

    private static bool TryReadIntegrityRid(IntPtr processHandle, out int integrityRid)
    {
        integrityRid = default;
        if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            return false;
        }

        try
        {
            _ = GetTokenInformation(
                tokenHandle,
                TokenIntegrityLevel,
                IntPtr.Zero,
                tokenInformationLength: 0,
                out var requiredLength);
            if (requiredLength <= 0)
            {
                return false;
            }

            var tokenInformation = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(
                        tokenHandle,
                        TokenIntegrityLevel,
                        tokenInformation,
                        requiredLength,
                        out _))
                {
                    return false;
                }

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(tokenInformation);
                if (label.Label.Sid == IntPtr.Zero)
                {
                    return false;
                }

                var subAuthorityCountPointer = GetSidSubAuthorityCount(label.Label.Sid);
                if (subAuthorityCountPointer == IntPtr.Zero)
                {
                    return false;
                }

                var subAuthorityCount = Marshal.ReadByte(subAuthorityCountPointer);
                if (subAuthorityCount == 0)
                {
                    return false;
                }

                var ridPointer = GetSidSubAuthority(label.Label.Sid, (uint)(subAuthorityCount - 1));
                if (ridPointer == IntPtr.Zero)
                {
                    return false;
                }

                integrityRid = Marshal.ReadInt32(ridPointer);
                return integrityRid >= 0;
            }
            finally
            {
                Marshal.FreeHGlobal(tokenInformation);
            }
        }
        finally
        {
            _ = CloseHandle(tokenHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SidAndAttributes
    {
        internal readonly IntPtr Sid;
        internal readonly uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenMandatoryLabel
    {
        internal readonly SidAndAttributes Label;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
}
