using System.Runtime.CompilerServices;
using VerifyNUnit;
using VerifyTests;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

public static class VerifySetup
{
    [ModuleInitializer]
    public static void Initialize()
    {
        Verifier.UseProjectRelativeDirectory("Snapshots");
        VerifierSettings.IgnoreMember<InteractionEffects>(effects => effects.ForegroundActivated);
    }
}
