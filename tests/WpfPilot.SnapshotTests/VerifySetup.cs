using System.Runtime.CompilerServices;
using VerifyNUnit;

namespace WpfPilot.SnapshotTests;

public static class VerifySetup
{
    [ModuleInitializer]
    public static void Initialize()
    {
        Verifier.UseProjectRelativeDirectory("Snapshots");
    }
}

