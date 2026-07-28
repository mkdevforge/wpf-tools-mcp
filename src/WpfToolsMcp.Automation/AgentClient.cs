using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Automation;

internal sealed class AgentClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan DefaultCallTimeout = ResolveDefaultCallTimeout();

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposeStarted;
    private int _faulted;

    private AgentClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
    }

    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _faulted) != 0 || Volatile.Read(ref _disposeStarted) != 0)
            {
                return false;
            }

            try
            {
                return _pipe.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public static async Task<AgentClient> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromSeconds(5);
        }

        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await pipe.ConnectAsync(cts.Token);
            return new AgentClient(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public async Task<T> CallAsync<T>(string method, object? @params, CancellationToken cancellationToken)
    {
        var paramsNode = @params is null ? null : JsonSerializer.SerializeToNode(@params, JsonOptions);
        var result = await CallRawAsync(method, paramsNode, cancellationToken);
        var value = result is null ? default : result.Deserialize<T>(JsonOptions);
        if (value is null)
        {
            throw new InvalidOperationException($"Agent call '{method}' returned null.");
        }

        return value;
    }

    public async Task<JsonNode?> CallRawAsync(string method, JsonNode? @params, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

        if (!_pipe.IsConnected)
        {
            throw new InvalidOperationException("Agent pipe is not connected.");
        }

        var request = new AgentRequest(Guid.NewGuid().ToString("N"), method, @params);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        cts.CancelAfter(DefaultCallTimeout);
        var callToken = cts.Token;

        var lockTaken = false;
        var ioStarted = false;
        var responseReceived = false;
        try
        {
            await _mutex.WaitAsync(callToken);
            lockTaken = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            if (Volatile.Read(ref _faulted) != 0 || !_pipe.IsConnected)
            {
                throw new InvalidOperationException("Agent pipe is not connected.");
            }

            ioStarted = true;
            await PipeProtocol.WriteAsync(_pipe, request, callToken);
            var response = await PipeProtocol.ReadAsync<AgentResponse>(_pipe, callToken);
            responseReceived = true;

            if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
            {
                PoisonConnection();
                throw new InvalidOperationException("Agent protocol error: response ID mismatch.");
            }

            if (!response.Ok)
            {
                var message = response.Error?.Message ?? "Agent call failed.";
                var details = response.Error?.Details;
                if (!string.IsNullOrWhiteSpace(details))
                {
                    message += $"{Environment.NewLine}{details}";
                }

                throw new InvalidOperationException(message);
            }

            return response.Result;
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(AgentClient));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (ioStarted)
            {
                PoisonConnection();
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            if (ioStarted)
            {
                PoisonConnection();
            }

            throw new TimeoutException(
                $"Agent call '{method}' timed out after {DefaultCallTimeout.TotalSeconds:0.###}s. " +
                "Set WPF_TOOLS_MCP_AGENT_CALL_TIMEOUT_MS to override.");
        }
        catch (Exception) when (
            _disposeCts.IsCancellationRequested &&
            Volatile.Read(ref _faulted) == 0)
        {
            throw new ObjectDisposedException(nameof(AgentClient));
        }
        catch
        {
            if (ioStarted && !responseReceived)
            {
                PoisonConnection();
            }

            throw;
        }
        finally
        {
            if (lockTaken)
            {
                _mutex.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _disposeCts.Cancel();
        _pipe.Dispose();

        await _mutex.WaitAsync();
        _mutex.Release();
        _mutex.Dispose();
        _disposeCts.Dispose();
    }

    private void PoisonConnection()
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        try
        {
            _pipe.Dispose();
        }
        catch
        {
        }

        _ = CompleteFaultedDisposalAsync();
    }

    private async Task CompleteFaultedDisposalAsync()
    {
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static TimeSpan ResolveDefaultCallTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_AGENT_CALL_TIMEOUT_MS");
        if (int.TryParse(raw, out var ms) && ms > 0)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        return TimeSpan.FromSeconds(10);
    }
}
