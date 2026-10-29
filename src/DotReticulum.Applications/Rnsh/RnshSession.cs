using System.Diagnostics;
using DotReticulum.Core;
using DotReticulum.Transport;
using MessageChannel = System.Threading.Channels.Channel<DotReticulum.Transport.MessageBase>;

namespace DotReticulum.Applications.Rnsh;

/// <summary>One owned, bounded rnsh v1 channel session; independent of rnx requests.</summary>
public sealed class RnshSession : IAsyncDisposable
{
    private readonly Link _link;
    private readonly DotReticulum.Transport.Channel _channel;
    private readonly CancellationTokenSource _closed = new();
    private readonly SemaphoreSlim _send = new(1);
    private readonly MessageChannel _messages = System.Threading.Channels.Channel.CreateBounded<MessageBase>(64);
    private readonly Func<MessageBase, bool> _handler;
    private readonly Action<Link>? _previousClosed;

    public RnshSession(Link link)
    {
        _link = link;
        _handler = message =>
        {
            if (!_messages.Writer.TryWrite(message))
            {
                _messages.Writer.TryComplete(new IOException("rnsh receive queue exhausted."));
                link.Teardown();
            }
            return true;
        };
        _channel = link.GetChannel(channel =>
        {
            RnshMessage.Register(channel);
            channel.AddMessageHandler(_handler);
        });
        _previousClosed = link.Closed;
        link.Closed = l =>
        {
            _closed.Cancel();
            _messages.Writer.TryComplete(new IOException("rnsh link closed."));
            _previousClosed?.Invoke(l);
        };
    }

    public async Task<Envelope> SendAsync(MessageBase message, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token);
        await _send.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            while (!_channel.IsReadyToSend)
            {
                if (_channel.Completion.IsCompleted) throw new IOException("rnsh channel closed.");
                await Task.Delay(10, linked.Token).ConfigureAwait(false);
            }
            return _channel.Send(message);
        }
        finally { _send.Release(); }
    }

    private async Task<MessageBase> ReceiveAsync(CancellationToken token) =>
        await _messages.Reader.ReadAsync(token).ConfigureAwait(false);

    public async Task<int> InitiateAsync(RnshExecute command, Stream stdin, Stream stdout, Stream stderr,
        TimeSpan handshakeTimeout, CancellationToken token, bool interactive = false, Func<RnshWindow>? window = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            handshake.CancelAfter(handshakeTimeout);
            await SendAsync(new RnshVersion(), handshake.Token).ConfigureAwait(false);
            var version = await ReceiveAsync(handshake.Token).ConfigureAwait(false);
            if (version is RnshError failure) throw new IOException(failure.Error);
            if (version is not RnshVersion { Protocol: 1 }) throw new IOException("Incompatible rnsh protocol.");
            await SendAsync(command, handshake.Token).ConfigureAwait(false);
        }
        var input = PumpInputAsync(stdin, stdout, interactive, lifetime.Token);
        var resize = window is null ? Task.CompletedTask : PumpWindowAsync(command.Window, window, lifetime.Token);
        try
        {
            while (true)
            {
                var received = ReceiveAsync(lifetime.Token);
                var ready = await Task.WhenAny(received, input).ConfigureAwait(false);
                if (ready == input && input.IsFaulted) await input.ConfigureAwait(false);
                var message = await received.ConfigureAwait(false);
                switch (message)
                {
                    case RnshStream { StreamId: 1 or 2 } data:
                        var output = data.StreamId == 1 ? stdout : stderr;
                        await output.WriteAsync(data.Data, lifetime.Token).ConfigureAwait(false);
                        await output.FlushAsync(lifetime.Token).ConfigureAwait(false);
                        break;
                    case RnshExited exited: return exited.ReturnCode;
                    case RnshError { Fatal: true } failure: throw new IOException(failure.Error);
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await ObserveCancellationAsync(resize).ConfigureAwait(false);
            await ObserveCancellationAsync(input).ConfigureAwait(false);
        }
    }

    private async Task PumpWindowAsync(RnshWindow initial, Func<RnshWindow> window, CancellationToken token)
    {
        var last = initial.Pack();
        long? lastSent = null;
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
            var size = window();
            var current = size.Pack();
            if (!last.AsSpan().SequenceEqual(current) &&
                (lastSent is null || Stopwatch.GetElapsedTime(lastSent.Value).TotalSeconds >= (_link.Rtt ?? 0.1) * 25))
            {
                await SendAsync(size, token).ConfigureAwait(false);
                last = current;
                lastSent = Stopwatch.GetTimestamp();
            }
        }
    }

    private async Task PumpInputAsync(Stream input, Stream output, bool interactive, CancellationToken token)
    {
        try
        {
            var buffer = new byte[Math.Max(1, _channel.Mdu - 2)];
            var escapes = interactive ? new RnshEscapes() : null;
            while (true)
            {
                var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                var data = buffer[..count];
                if (escapes is not null)
                {
                    data = escapes.Process(data, out var local);
                    if (local.Length > 0) { await output.WriteAsync(local, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); }
                    if (escapes.Terminate) { _link.Teardown(); return; }
                }
                if (data.Length > 0 || count == 0)
                    await SendAsync(new RnshStream { StreamId = 0, Data = data, Eof = count == 0 }, token).ConfigureAwait(false);
                if (count == 0) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { _link.Teardown(); }
    }

    public async Task ServeAsync(RnshListenerOptions options, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token);
        RnshProcess? child = null;
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask, receiver = Task.CompletedTask;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, (_link.Rtt ?? 1) * 10)));
                while (!options.AllowAll && _link.RemoteIdentity is null)
                    await Task.Delay(10, handshake.Token).ConfigureAwait(false);
                if (!options.AllowAll && !options.IsAllowed(_link.RemoteIdentity!.Hash))
                    throw new IOException("Identity not allowed");
                var version = await ReceiveAsync(handshake.Token).ConfigureAwait(false);
                if (version is not RnshVersion { Protocol: 1 }) throw new IOException("Incompatible protocol");
                await SendAsync(new RnshVersion(), handshake.Token).ConfigureAwait(false);
                if (await ReceiveAsync(handshake.Token).ConfigureAwait(false) is not RnshExecute command)
                    throw new IOException("Protocol error (WAIT_CMD)");
                if (!options.AllowRemoteCommand && command.Command.Length != 0)
                    throw new IOException("Remote command line not allowed by listener");
                var argv = command.Command.Length == 0 ? options.DefaultCommand :
                    options.RemoteCommandAsArguments ? [.. options.DefaultCommand, .. command.Command] : command.Command;
                child = RnshProcess.Start(argv, command, _link.RemoteIdentity);
            }
            stdout = PumpOutputAsync(child.Stdout, 1, lifetime.Token);
            stderr = PumpOutputAsync(child.Stderr, 2, lifetime.Token);
            receiver = ReceiveInputAsync(child, lifetime.Token);
            var exited = child.WaitAsync(lifetime.Token);
            var done = await Task.WhenAny(exited, receiver).ConfigureAwait(false);
            if (done == receiver) await receiver.ConfigureAwait(false);
            var code = await exited.ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token).ConfigureAwait(false);
            var final = await SendAsync(new RnshExited { ReturnCode = code }, lifetime.Token).ConfigureAwait(false);
            using var receiptDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            receiptDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, (_link.Rtt ?? 1) * 20)));
            while (final.State is MessageState.New or MessageState.Sent)
                await Task.Delay(10, receiptDeadline.Token).ConfigureAwait(false);
            if (final.State != MessageState.Delivered) throw new IOException("rnsh exit message was not acknowledged.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
            or OperationCanceledException or System.Threading.Channels.ChannelClosedException or ArgumentException or TimeoutException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    var fatal = await SendAsync(new RnshError { Error = ex.Message, Fatal = true }, deadline.Token).ConfigureAwait(false);
                    while (fatal.State is MessageState.New or MessageState.Sent)
                        await Task.Delay(10, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception sendError) when (sendError is IOException or OperationCanceledException) { }
            }
            options.Log?.Invoke($"rnsh session: {ex.Message}");
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (child is not null) await child.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(ObserveCancellationAsync(stdout), ObserveCancellationAsync(stderr), ObserveCancellationAsync(receiver))
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _link.Teardown();
        }
    }

    private async Task ReceiveInputAsync(RnshProcess child, CancellationToken token)
    {
        while (true)
        {
            switch (await ReceiveAsync(token).ConfigureAwait(false))
            {
                case RnshStream { StreamId: 0 } input:
                    if (input.Data.Length > 0) await child.Stdin.WriteAsync(input.Data, token).ConfigureAwait(false);
                    if (input.Eof) child.CloseInput();
                    break;
                case RnshWindow window: child.Resize(window); break;
                case RnshNoop noop: await SendAsync(noop, token).ConfigureAwait(false); break;
                case RnshStream: throw new IOException("Invalid input stream");
            }
        }
    }

    private async Task PumpOutputAsync(Stream output, int stream, CancellationToken token)
    {
        var buffer = new byte[Math.Max(1, _channel.Mdu - 2)];
        while (true)
        {
            var count = await output.ReadAsync(buffer, token).ConfigureAwait(false);
            await SendAsync(new RnshStream { StreamId = stream, Data = buffer[..count], Eof = count == 0 }, token).ConfigureAwait(false);
            if (count == 0) return;
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (System.Threading.Channels.ChannelClosedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.RemoveMessageHandler(_handler);
        _closed.Cancel();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _link.TeardownAsync(deadline.Token).ConfigureAwait(false);
    }
}

public sealed class RnshListenerOptions
{
    public bool AllowAll { get; init; }
    public bool AllowRemoteCommand { get; init; } = true;
    public bool RemoteCommandAsArguments { get; init; }
    public string[] DefaultCommand { get; init; } = [OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh"];
    public Func<byte[], bool> IsAllowed { get; init; } = _ => false;
    public Action<string>? Log { get; init; }
}
