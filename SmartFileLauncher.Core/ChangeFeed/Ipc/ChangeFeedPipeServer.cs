using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedPipeServer
{
    private static readonly TimeSpan AbandonedConnectionBackoff = TimeSpan.FromMilliseconds(10);

    private readonly ChangeFeedAdmissionService _admission;
    private readonly Action<Exception>? _onFault;
    private readonly Func<bool, NamedPipeServerStream> _createInstance;

    private int _waiting;

    public ChangeFeedPipeServer(
        ChangeFeedAdmissionService admission,
        string pipeName = ChangeFeedProtocol.PipeName,
        SecurityIdentifier? additionalServerPrincipal = null,
        Action<Exception>? onFault = null)
        : this(admission, pipeName, additionalServerPrincipal, onFault, null)
    {
    }

    internal ChangeFeedPipeServer(
        ChangeFeedAdmissionService admission,
        string pipeName,
        SecurityIdentifier? additionalServerPrincipal,
        Action<Exception>? onFault,
        Func<bool, NamedPipeServerStream>? createInstance)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _onFault = onFault;
        _createInstance = createInstance
            ?? (firstInstance => ChangeFeedPipeFactory.Create(
                pipeName,
                firstInstance,
                additionalServerPrincipal));
    }

    public int AvailableSlots => Volatile.Read(ref _waiting);

    public async Task ListenAsync(CancellationToken cancellationToken)
    {
        var seeds = new NamedPipeServerStream[ChangeFeedProtocol.MaximumConcurrentConnections];

        try
        {
            for (var index = 0; index < seeds.Length; index++)
            {
                seeds[index] = _createInstance(index == 0);
            }
        }
        catch
        {
            foreach (var seed in seeds)
            {
                seed?.Dispose();
            }

            throw;
        }

        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var faultGate = new object();
        Exception? fatal = null;

        void Fail(Exception failure)
        {
            lock (faultGate)
            {
                fatal ??= failure;
            }

            listening.Cancel();
        }

        var workers = new Task[seeds.Length];
        for (var index = 0; index < seeds.Length; index++)
        {
            workers[index] = AcceptLoopAsync(seeds[index], listening.Token, Fail);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        if (fatal is not null && !cancellationToken.IsCancellationRequested)
        {
            throw fatal;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task AcceptLoopAsync(
        NamedPipeServerStream seed,
        CancellationToken listeningToken,
        Action<Exception> fail)
    {
        await Task.Yield();

        var pipe = seed;

        try
        {
            while (!listeningToken.IsCancellationRequested)
            {
                var accepted = false;
                Interlocked.Increment(ref _waiting);

                try
                {
                    await pipe.WaitForConnectionAsync(listeningToken).ConfigureAwait(false);
                    accepted = true;
                }
                catch (OperationCanceledException) when (listeningToken.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException failure)
                {
                    Report(failure);
                }
                catch (Exception failure)
                {
                    fail(failure);
                    return;
                }
                finally
                {
                    Interlocked.Decrement(ref _waiting);
                }

                if (listeningToken.IsCancellationRequested)
                {
                    return;
                }

                if (accepted)
                {
                    try
                    {
                        await ExchangeAsync(pipe, listeningToken).ConfigureAwait(false);
                    }
                    catch (Exception failure)
                    {
                        Report(failure);
                    }
                }
                else
                {
                    try
                    {
                        await Task.Delay(AbandonedConnectionBackoff, listeningToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                NamedPipeServerStream replacement;
                try
                {
                    replacement = _createInstance(false);
                }
                catch (Exception failure)
                {
                    fail(failure);
                    return;
                }

                pipe.Dispose();
                pipe = replacement;
            }
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private void Report(Exception failure)
    {
        if (_onFault is null)
        {
            return;
        }

        try
        {
            _onFault(failure);
        }
        catch
        {
        }
    }

    public async Task ExchangeAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ChangeFeedProtocol.IoTimeout);

        ChangeFeedResponse response;
        try
        {
            var request = await ChangeFeedMessageChannel
                .ReadAsync<ChangeFeedRequest>(pipe, deadline.Token)
                .ConfigureAwait(false);

            response = _admission.Handle(pipe, request);
        }
        catch (ChangeFeedProtocolException failure)
        {
            response = ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.InvalidRequest,
                failure.Message);
        }
        catch (ChangeFeedImpersonationException failure)
        {
            response = ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.RootUnauthorized,
                failure.Message);
        }

        await ChangeFeedMessageChannel
            .WriteAsync(pipe, response, deadline.Token)
            .ConfigureAwait(false);
    }
}
