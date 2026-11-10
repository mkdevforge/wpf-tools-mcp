using WpfToolsMcp.Automation;
using WpfToolsMcp.McpServer;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ServerOptionsTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private static ServerOptions Parse(string[] args, Dictionary<string, string>? environment = null) =>
        ServerOptions.Parse(args, name => environment is not null && environment.TryGetValue(name, out var value) ? value : null);

    [Test]
    public void Defaults_to_stdio_and_the_core_profile()
    {
        var options = Parse(["--some-host-arg", "x"]);
        Assert.Multiple(() =>
        {
            Assert.That(options.Transport, Is.EqualTo(ServerTransport.Stdio));
            Assert.That(options.Profile, Is.EqualTo(ToolProfile.Core));
            Assert.That(options.PathMappings, Is.Empty);
            Assert.That(options.HostArgs, Is.EqualTo(new[] { "--some-host-arg", "x" }));
        });
    }

    [Test]
    public void Reads_the_tool_profile_from_arguments_before_the_environment()
    {
        var environment = new Dictionary<string, string> { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = "core" };
        Assert.Multiple(() =>
        {
            Assert.That(Parse(["--tool-profile", "diagnostics"], environment).Profile, Is.EqualTo(ToolProfile.Diagnostics));
            Assert.That(Parse(["--tool-profile=full"]).Profile, Is.EqualTo(ToolProfile.Diagnostics));
            Assert.That(Parse([], new() { ["WPF_TOOLS_MCP_TOOL_PROFILE"] = "diagnostics" }).Profile, Is.EqualTo(ToolProfile.Diagnostics));
        });
    }

    [Test]
    public void Http_on_loopback_works_without_a_token()
    {
        var options = Parse(["--transport", "http"]);
        Assert.Multiple(() =>
        {
            Assert.That(options.Transport, Is.EqualTo(ServerTransport.Http));
            Assert.That(options.HttpUrl, Is.EqualTo(new Uri(ServerOptions.DefaultHttpUrl)));
            Assert.That(options.HttpToken, Is.Null);
            Assert.That(options.HttpListensBeyondLoopback, Is.False);
        });
    }

    [TestCase("http://0.0.0.0:5765/mcp")]
    [TestCase("http://192.168.1.20:5765/mcp")]
    [TestCase("http://[::]:5765/mcp")]
    public void Http_beyond_loopback_requires_a_token(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() => Parse(["--transport", "http", "--http-url", url]));
        Assert.That(ex!.Message, Does.Contain("requires an access token"));

        var options = Parse(["--transport", "http", "--http-url", url], new() { ["WPF_TOOLS_MCP_HTTP_TOKEN"] = Token });
        Assert.Multiple(() =>
        {
            Assert.That(options.HttpToken, Is.EqualTo(Token));
            Assert.That(options.HttpListensBeyondLoopback, Is.True);
        });
    }

    [Test]
    public void Reads_the_token_from_a_file()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wpf-tools-mcp-token-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, $"  {Token}\r\n");
        try
        {
            var options = Parse(["--transport", "http", "--http-url", "http://0.0.0.0:5765/mcp", "--http-token-file", file]);
            Assert.That(options.HttpToken, Is.EqualTo(Token));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public void Rejects_short_tokens_and_unreadable_token_files()
    {
        Assert.Throws<ArgumentException>(() =>
            Parse(["--transport", "http"], new() { ["WPF_TOOLS_MCP_HTTP_TOKEN"] = "short" }));
        Assert.Throws<ArgumentException>(() =>
            Parse(["--transport", "http", "--http-token-file", @"C:\does-not-exist\token.txt"]));
    }

    [TestCase("https://127.0.0.1:5765/mcp")]
    [TestCase("http://qa-vm:5765/mcp")]
    [TestCase("http://127.0.0.1:5765/mcp?x=1")]
    [TestCase("not a url")]
    public void Rejects_unsupported_urls(string url) =>
        Assert.Throws<ArgumentException>(() => Parse(["--transport", "http", "--http-url", url]));

    [Test]
    public void Http_settings_require_the_http_transport() =>
        Assert.Throws<ArgumentException>(() => Parse(["--http-url", "http://127.0.0.1:5765/mcp"]));

    [Test]
    public void Rejects_unknown_transports() =>
        Assert.Throws<ArgumentException>(() => Parse(["--transport", "pipe"]));

    [Test]
    public void Reads_path_maps_from_arguments_or_the_environment()
    {
        var fromArguments = Parse(["--path-map", @"C:\a=D:\b", "--path-map", @"C:\c=D:\d"]);
        var fromEnvironment = Parse([], new() { ["WPF_TOOLS_MCP_PATH_MAP"] = @"C:\a=D:\b | C:\c=D:\d" });
        var expected = new[] { new PathMapping(@"C:\a", @"D:\b"), new PathMapping(@"C:\c", @"D:\d") };
        Assert.Multiple(() =>
        {
            Assert.That(fromArguments.PathMappings, Is.EqualTo(expected));
            Assert.That(fromEnvironment.PathMappings, Is.EqualTo(expected));
        });
    }

    [Test]
    public void Reads_the_artifact_directory() =>
        Assert.That(Parse(["--artifact-dir", @"C:\artifacts"]).ArtifactDirectory, Is.EqualTo(@"C:\artifacts"));
}
