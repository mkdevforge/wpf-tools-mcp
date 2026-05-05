using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Agent;

internal static class AgentServer
{
    private static readonly UiThreadLatencyRecorder UiThreadLatency = new();
    private static readonly AgentOperationRegistry Operations = AgentOperations.Create();

    public static async Task RunAsync(string pipeName, CancellationToken cancellationToken)
    {
        var connectionTasks = new AgentConnectionTasks();
        var retryDelay = AgentPipeRetryPolicy.InitialDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe(pipeName);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                retryDelay = AgentPipeRetryPolicy.InitialDelay;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex) when (AgentPipeRetryPolicy.CanRetry(ex))
            {
                pipe?.Dispose();
                await AgentPipeRetryPolicy.DelayAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = AgentPipeRetryPolicy.NextDelay(retryDelay);
                continue;
            }
            catch
            {
                pipe?.Dispose();
                throw;
            }

            connectionTasks.Track(Task.Run(() => RunConnectionAsync(pipe, cancellationToken), CancellationToken.None));
        }

        await connectionTasks.WaitForAllAsync().ConfigureAwait(false);
    }

    private static NamedPipeServerStream CreatePipe(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task RunConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using var _ = pipe.ConfigureAwait(false);

        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            AgentRequest request;
            try
            {
                request = await PipeProtocol.ReadAsync<AgentRequest>(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            AgentResponse response;
            try
            {
                response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await PipeProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
        }
    }

    internal static async Task<AgentResponse> HandleAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!Operations.TryGet(request.Method, out var operation))
            {
                return AgentResponses.UnknownMethod(request.Id, request.Method);
            }

            var context = new AgentOperationContext(UiThreadLatency);
            if (!operation.RequiresUiThread)
            {
                return operation.Handle(request, context, cancellationToken);
            }

            return await RunOnUiAsync(
                () => operation.Handle(request, context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return AgentResponses.Failure(request.Id, AgentErrorCodes.OperationCanceled, ex.Message, ex.ToString());
        }
        catch (Exception ex)
        {
            return AgentResponses.FromException(request.Id, ex);
        }
    }

    private static async Task<AgentResponse> RunOnUiAsync(Func<AgentResponse> action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw AgentOperationException.DispatcherUnavailable();

        if (dispatcher.CheckAccess())
        {
            return action();
        }

        var op = dispatcher.InvokeAsync(action, DispatcherPriority.Send, cancellationToken);
        return await op.Task;
    }
}
