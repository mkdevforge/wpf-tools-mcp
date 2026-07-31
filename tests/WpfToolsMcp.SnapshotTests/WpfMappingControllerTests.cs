using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.UIA3;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class WpfMappingControllerTests
{
    private const string NativeButtonName = "Native mapping target";

    [Test]
    public async Task Known_native_window_keeps_uia_output_and_completes_unmapped_without_agent_access()
    {
        using var nativeWindow = await NativeWindowHost.StartAsync();
        using var controller = new AutomationController();
        AttachControllerToCurrentProcess(controller);
        SetNonRetryableAgentFailure(controller);

        var response = await controller.GetUiaLocatorsAsync(
            locator: new ElementLocator(Name: NativeButtonName),
            windowHandle: nativeWindow.WindowHandle,
            backend: InspectionBackend.Uia);

        Assert.Multiple(() =>
        {
            Assert.That(response.Uia?.Name, Is.EqualTo(NativeButtonName));
            Assert.That(response.Uia?.ElementId, Does.StartWith("uia_"));
            Assert.That(response.LocatorSuggestions, Is.Not.Null);
            Assert.That(response.FlaUi, Is.Not.Null);
            Assert.That(response.Wpf, Is.Null);
            Assert.That(response.WpfMapping?.Available, Is.True);
            Assert.That(response.WpfMapping?.Method, Is.EqualTo("frameworkClassification"));
            Assert.That(response.WpfMapping?.Status, Is.EqualTo(ElementMappingStatus.Unmapped));
            Assert.That(response.WpfMapping?.ScanComplete, Is.True);
            Assert.That(response.WpfMapping?.Failure, Is.Null);
            Assert.That(response.WpfMapping?.Evidence, Does.Contain("window_framework_not_wpf"));
        });
    }

    [Test]
    public async Task Unavailable_agent_keeps_valid_uia_output_and_returns_mapping_failure()
    {
        using var controller = new AutomationController();
        var executable = TestAppPaths.FindTestAppExecutable();
        _ = await controller.LaunchAsync(new LaunchAppRequest(
            ExePath: executable,
            WorkingDirectory: Path.GetDirectoryName(executable)!));

        try
        {
            SetNonRetryableAgentFailure(controller);

            var response = await controller.GetUiaLocatorsAsync(
                locator: new ElementLocator(AutomationId: "Basic_Button"),
                backend: InspectionBackend.Uia);

            Assert.Multiple(() =>
            {
                Assert.That(response.Uia?.AutomationId, Is.EqualTo("Basic_Button"));
                Assert.That(response.Uia?.ElementId, Does.StartWith("uia_"));
                Assert.That(response.LocatorSuggestions, Is.Not.Null);
                Assert.That(response.FlaUi, Is.Not.Null);
                Assert.That(response.Wpf, Is.Null);
                Assert.That(response.WpfMapping?.Available, Is.False);
                Assert.That(response.WpfMapping?.Status, Is.Null);
                Assert.That(response.WpfMapping?.Failure?.Code, Is.EqualTo(FailureDiagnostics.Codes.UnsupportedArchitecture));
                Assert.That(response.WpfMapping?.Failure?.Stage, Is.EqualTo(FailureDiagnostics.Stages.ArchitectureDetection));
                Assert.That(response.WpfMapping?.Evidence, Does.Contain("mapping_backend_unavailable"));
            });
        }
        finally
        {
            try
            {
                _ = await controller.CloseAsync(new CloseAppRequest(Force: true, TimeoutMs: 2_000));
            }
            catch
            {
            }
        }
    }

    private static void SetNonRetryableAgentFailure(AutomationController controller) =>
        controller.SetAutoAgentFailure(
            new NotSupportedException("Test-only unavailable WPF backend."),
            FailureDiagnostics.Stages.ArchitectureDetection);

    private static void AttachControllerToCurrentProcess(AutomationController controller)
    {
        SetPrivateField(controller, "_application", Application.Attach(Environment.ProcessId));
        SetPrivateField(controller, "_automation", new UIA3Automation());
        SetPrivateField(
            controller,
            "_processIdentity",
            (ProcessInstanceIdentity?)ProcessTargetResolver.ResolveByPid(Environment.ProcessId).Identity);
    }

    private static void SetPrivateField<T>(AutomationController controller, string name, T value)
    {
        var field = typeof(AutomationController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Missing AutomationController field '{name}'.");
        field.SetValue(controller, value);
    }

    private sealed class NativeWindowHost : IDisposable
    {
        private const uint WsOverlappedWindow = 0x00CF0000;
        private const uint WsVisible = 0x10000000;
        private const uint WsChild = 0x40000000;
        private const uint WsTabStop = 0x00010000;
        private const uint BsPushButton = 0x00000000;
        private const uint WmQuit = 0x0012;
        private const int SwShowNoActivate = 4;

        private readonly Thread _thread;
        private readonly TaskCompletionSource<long> _windowCreated = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private uint _nativeThreadId;
        private IntPtr _windowHandle;
        private int _disposed;

        private NativeWindowHost()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "WPF Tools MCP native-window test host"
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public long WindowHandle => _windowHandle.ToInt64();

        public static async Task<NativeWindowHost> StartAsync()
        {
            var host = new NativeWindowHost();
            host._thread.Start();
            try
            {
                _ = await host._windowCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return host;
            }
            catch
            {
                host.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_nativeThreadId != 0)
            {
                _ = PostThreadMessage(_nativeThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            }

            _ = _thread.Join(TimeSpan.FromSeconds(5));
        }

        private void Run()
        {
            _nativeThreadId = GetCurrentThreadId();
            try
            {
                var module = GetModuleHandle(null);
                _windowHandle = CreateWindowEx(
                    0,
                    "STATIC",
                    "Native WPF mapping test",
                    WsOverlappedWindow | WsVisible,
                    -10_000,
                    -10_000,
                    320,
                    180,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    module,
                    IntPtr.Zero);
                if (_windowHandle == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var button = CreateWindowEx(
                    0,
                    "BUTTON",
                    NativeButtonName,
                    WsChild | WsVisible | WsTabStop | BsPushButton,
                    20,
                    20,
                    180,
                    40,
                    _windowHandle,
                    new IntPtr(101),
                    module,
                    IntPtr.Zero);
                if (button == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                _ = ShowWindow(_windowHandle, SwShowNoActivate);
                _ = UpdateWindow(_windowHandle);
                _windowCreated.TrySetResult(_windowHandle.ToInt64());

                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    _ = TranslateMessage(ref message);
                    _ = DispatchMessage(ref message);
                }
            }
            catch (Exception ex)
            {
                _windowCreated.TrySetException(ex);
            }
            finally
            {
                if (_windowHandle != IntPtr.Zero)
                {
                    _ = DestroyWindow(_windowHandle);
                    _windowHandle = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int PointX;
            public int PointY;
            public uint Private;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr windowHandle, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateWindow(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(
            out NativeMessage message,
            IntPtr windowHandle,
            uint minimumMessage,
            uint maximumMessage);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMessage message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(
            uint threadId,
            uint message,
            IntPtr wParam,
            IntPtr lParam);
    }
}
