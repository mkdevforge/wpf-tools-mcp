namespace WpfToolsMcp.Contracts;

using System.Text.Json.Serialization;

/// <summary>
/// Whether the server's Windows session can receive physical input and produce screen captures.
/// <see cref="Available"/> is false only on positive evidence; checks that could not be read are
/// listed in <see cref="UnverifiedChecks"/> instead of being assumed to pass or fail.
/// </summary>
public sealed record InteractiveDesktopStatus(
    bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UnavailableReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionConnectState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SessionLocked = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WindowStation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServerDesktop = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputDesktop = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ScreensaverRunning = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? UnverifiedChecks = null);

public sealed record ServerProcessEnvironment(
    int ProcessId,
    string ProcessArchitecture,
    string Runtime);

public sealed record OperatingSystemEnvironment(
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EditionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Build = null);

/// <summary>Raw firmware-reported system identity; virtual machines usually name their hypervisor here.</summary>
public sealed record HardwareEnvironment(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SystemManufacturer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SystemProductName = null);

public sealed record DisplayAdapterInfo(
    string DisplayDeviceName,
    string AdapterName);

public sealed record DisplayEnvironment(
    Rect VirtualScreen,
    IReadOnlyList<DisplayInfo> Displays,
    IReadOnlyList<DisplayAdapterInfo> Adapters);

/// <summary>
/// WPF render tier as seen by the server process (0 = software, 1 = partial hardware, 2 = full hardware).
/// Target applications may still force software rendering on their own.
/// </summary>
public sealed record RenderingEnvironment(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WpfRenderTier = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UnavailableReason = null);

public sealed record GetServerEnvironmentResponse(
    ServerProcessEnvironment Server,
    OperatingSystemEnvironment OperatingSystem,
    HardwareEnvironment Hardware,
    InteractiveDesktopStatus InteractiveDesktop,
    DisplayEnvironment Displays,
    RenderingEnvironment Rendering);
