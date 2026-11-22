# Channel and Buffer

A *channel* provides reliable, ordered delivery of typed messages over an established link. You define message classes, register them with the link's channel, and send them. Reticulum takes care of sequencing, retransmission and windowing, and your handlers receive each message once, in order.

A *buffer* builds on a channel to give you ordinary byte streams. Each stream has a numeric ID, so several independent streams can share one channel. This is useful when you want to move a continuous flow of data, such as a shell session or a file pipe, rather than discrete messages.

## Using a channel

A message type derives from `MessageBase`, supplies a `MsgType` that is unique on the channel, and packs and unpacks its own payload. Values from `0xF000` upward are reserved.

```csharp
sealed class TextMessage : MessageBase
{
    public override ushort MsgType => 0x0101;
    public string Text { get; set; } = "";
    public override byte[] Pack() => Encoding.UTF8.GetBytes(Text);
    public override void Unpack(ReadOnlySpan<byte> raw) => Text = Encoding.UTF8.GetString(raw);
}

var channel = link.GetChannel(c =>
{
    c.RegisterMessageType<TextMessage>();
    c.AddMessageHandler(message =>
    {
        if (message is not TextMessage text) return false;
        Console.WriteLine(text.Text);
        return true;
    });
});

channel.Send(new TextMessage { Text = "Hello" });
```

`GetChannel(initialize)` registers types and handlers before ingress can acknowledge any data. `GetChannel()` returns the existing channel or creates one. Handlers are tried in registration order, and the first one returning true claims the message. `IsReadyToSend` tells you whether the send window currently has room.

Message constructors and `Pack`/`Unpack` are synchronous codec operations and must not do blocking application work.

## How a channel works

Each message travels in an envelope with a six-byte big-endian header: message type, sequence number and declared length. The declared length is tolerated even when it differs from the remaining payload, as in the reference.

* Messages are delivered once and in order, including across sequence-number wrap.
* The receiver holds up to 48 messages that arrive ahead of a gap.
* The send window grows as messages are delivered and adapts to the link's RTT. Retries contract it.
* A retry keeps the original sequence number and envelope.
* Link shutdown disconnects outstanding receipts.
* The largest outgoing message is 65,535 bytes, even on links with a larger MDU. An oversized envelope raises the typed `TooBig` channel error.

### Retry encryption

The pinned `Packet.resend` repacks the unchanged plaintext envelope. Link encryption therefore produces a fresh IV, ciphertext and packet hash on every retry, and each attempt gets a fresh receipt. A proof for the current hash settles the current receipt. A proof for an expired hash does not.

### Proofs and the bounded reorder window

The pinned reference checks a 48-message receive lookahead, but admits sends based only on the number of outstanding proofs (`Channel.py:353-395,398-440`). It also proves Channel packets before validating them against the receive window (`Link.py:1147-1155`). Proofs for later packets can therefore let a sender run far enough past an earlier lost sequence that data is discarded by the receiver yet acknowledged to the sender.

The original native implementation reproduced this against the real Python receiver: exactly 802,816 bytes arrived, the next expected sequence was 49, and EOF never came. Restricting only the .NET sender fixed outbound delivery, but the reverse direction still failed with the unchanged upstream sender.

DotReticulum now hardens both sides:

* The sender also caps the sequence distance from the oldest unproved envelope at `WindowMax`.
* The receiver proves retained and delivered frames, and duplicates, but not frames that are ahead of the window or malformed. Those are retried after the gap closes.
* Proof creation stays protected by the live link's ownership lock.

Wire formats, receive capacity and retry timers are unchanged. This is an intentional native reliability hardening, not a claim that the pinned implementation behaves the same way. No reference-side shim is used. Focused tests cover both boundaries across sequence wrap and duplicate re-proving, and the native and Python constrained flow delivers completely in both directions.

### Handlers and bounded work

Channel ordering and stream buffering run separately from optional message handlers, so a blocked handler cannot stall them.

| Bound | Limit |
| --- | --- |
| Optional message queue | 256 messages, one worker. Exhaustion closes the link. |
| Ahead-of-gap reorder ring | 48 messages |
| Optional handlers per channel | 64 |
| Stream readers per channel | 64 |
| Ready callbacks per reader | 64, with one worker and one coalesced notification |

Callbacks do not start a new task or thread per packet. Exceptions are logged with stream context, and disposal discards queued notifications.

Internal stream readers take precedence over public handlers. Unlike upstream's single registration-ordered callback list, an optional handler cannot consume or delay stream data. Registration order and first-true short-circuiting still apply to messages that no stream reader claims.

`Channel.HandlerCompletion` and `RawChannelReader.ReadyCallbacksCompletion` expose the current owned callback work. A synchronous callback that is already running cannot be forcibly cancelled and must return by itself. Shutdown and disposal release protocol waiters and pending work without waiting for it. Keep callbacks short, and never block on their own completion task.

## Using a buffer

`ChannelBuffer` creates native .NET streams over a channel:

```csharp
var (reader, writer) = ChannelBuffer.CreateBidirectional(receiveStreamId: 0, sendStreamId: 0, link.GetChannel());

await writer.WriteAsync("ping"u8.ToArray(), cancellationToken);

var buffer = new byte[4096];
int read = await reader.ReadAsync(buffer, cancellationToken);

await writer.CloseAsync(cancellationToken);
```

`CreateReader` and `CreateWriter` create one side at a time. You can pass a ready callback and a `maxBufferedBytes` limit for the reader.

On the wire, each chunk carries a two-byte stream header with an EOF flag, a compression flag and a 14-bit stream ID. Chunks are compressed when applicable, and decoded chunks are limited to 16,384 bytes. Negative stream IDs are rejected.

### Reading

Reads wait until data arrives, or until EOF, cancellation, disposal or link closure.

* Link closure drains already buffered bytes before returning EOF. A sender's EOF is terminal.
* Disposal throws `ObjectDisposedException` and clears storage.
* Zero-length reads return zero without consuming data.
* Cancelling a pending read leaves no orphaned wait on channel closure.

The reader uses one lazily allocated circular buffer, so reads do not copy or reallocate the remaining data. Its unread-byte capacity defaults to 1 MiB. When it is exhausted the reader raises `IOException`, releases its storage and closes the whole link.

That is the only honest option. The protocol has no message for crediting application consumption, and Channel proofs acknowledge retained data, not data the application has read. Blocking an ingress thread, or dropping a chunk that has already been proven, cannot provide real reader backpressure.

### Writing and closing

`WriteAsync` consumes all of the supplied memory or reports failure. It waits on the channel's real receipt and window signal, with cancellation, rather than polling. `WriteChunk` provides the upstream-style nonblocking partial write. There is no hidden write buffer that needs flushing.

Normal EOF drains buffered data first. Writes and close are serialized on each writer:

* A write already accepted finishes before EOF. New writes after close has started fail as disposed.
* Concurrent close calls share one operation. A cancelled or failed close can be retried without sending a duplicate EOF.
* Close has a monotonic 15-second deadline and reports a timeout instead of silently omitting EOF.
* Normal link shutdown ends close without attempting a post-close EOF.

## Differences from upstream

* **Proof and send-window hardening**, described in [Proofs and the bounded reorder window](#proofs-and-the-bounded-reorder-window).
* **Stream readers take precedence** over public handlers instead of sharing one ordered callback list.
* **Bounded callback work** uses fixed queues and workers instead of upstream's per-callback threads.
* **Blocking .NET streams.** Factories return `RawChannelReader` and `RawChannelWriter` streams, or a reader and writer tuple, not Python's `BufferedReader`, `BufferedWriter` or `BufferedRWPair` wrappers. Reads wait for data instead of returning `None` like Python's nonblocking `RawIOBase.readinto()`.
* **Reader overflow** fails the reader and closes the link rather than silently dropping data that has already been proven.
* **Close deadline.** The fixed 15-second deadline replaces upstream's `link.rtt * len(tx_ring)` estimate, which itself falls back to 15 seconds on an exception. EOF completion, idempotence and cancellation are explicit stream contracts rather than Python close quirks.
* **Validation.** Negative stream IDs are rejected rather than masked into a valid ID. An oversized envelope raises `TooBig` instead of Python's incidental `struct.error`.
* **Outlet readiness.** The .NET outlet reports a pending or closed link as not ready. Upstream's `is_usable` outlet predicate is explicitly unfinished and always returns true.

## Limitations

* A running synchronous callback cannot be forcibly interrupted.
* Live TCP bottlenecks cover 1,024 and 16,384 bytes, not every MTU or bearer.
* Sequence-wrap and RTT-boundary sweeps are reference-vector evidence, not live evidence under loss. Slow-reader overflow, callback races and close, write and disposal failure paths are covered by focused tests, not by a live combined-failure topology.
* In one repeated mesh run of the 16 MB soak case, one of three macOS executions timed out waiting for the Python return writer to complete. Later bounded repetitions passed, but that failure was not reproduced and its cause remains unresolved. Longer soak and broader bearers are unverified.

## Verification

### Reference sources

All sources are from RNS 1.5.7 at `a29bfb51c468945c96a1ee22cae0114e993e2391`.

| File | Lines | Contract |
| --- | --- | --- |
| `Channel.py` | 113–148 | Envelope header; declared length is ignored on decode |
| `Channel.py` | 175–245 | Windows, RTT thresholds, sequence modulus |
| `Channel.py` | 303–415 | Shutdown, insertion, receive order and duplicate handling |
| `Channel.py` | 417–544 | Delivery growth, retry contraction and timeouts, send reservation |
| `Channel.py` | 547–608 | Link outlet calls `Packet.send`/`resend` and reads its current hash |
| `Packet.py` | 180–247, 285–337, 417–436 | Pack encrypts Channel data; resend repacks; a new receipt takes the current packet hash |
| `Link.py` | 1166–1187 | Link encryption and decryption delegate to Token |
| `Cryptography/Token.py` | 87–116 | Fresh random IV on each encryption; authenticated decryption |
| `Transport.py` | 1352–1605 | Outbound registration, transmission and fresh receipt creation |
| `Buffer.py` | 44–98 | Stream flags and bounded compressed expansion |
| `Buffer.py` | 101–198 | Reader callbacks, EOF and buffering |
| `Buffer.py` | 201–294 | Compression attempts, chunk budget, close |

| File | SHA-256 |
| --- | --- |
| `Channel.py` | `5ab5cf0bfd2c0fd3574d53b405f48e08756c2105bba72d9bc530b652f985bdd7` |
| `Buffer.py` | `7f2269b25ada8e81b20f01692517e790bd3264dfdb461295570bcfd7b39b8db6` |
| `Packet.py` | `f1b12a7068c6036e20439f87f8bfb6ad2dd92689c03be387e2bb697d46bdb075` |
| `Link.py` | `4267ab1c5b11d9c415693298376f2a984aa9bca13a6245e9bc3dce3236365a7c` |
| `Transport.py` | `333b34eeab020946eefdd8a7e75d3fa74d2ffc5adfef7c0080752980f455bf2c` |
| `Cryptography/Token.py` | `8a75bbc724bce181b4a581f75c72b280f47e9a43db873f45ec576d4df6160b47` |

### Reference vectors and unit tests

Independent fixtures come from two generators in `tests\DotReticulum.Transport.Tests`. Both call `tests\Interop\reference_environment.py` before importing the reference. Older fixtures keep their original provenance.

* `GenerateParity5ChannelVectors.py` covers envelopes, windows and ordering. Its window and order fixture uses a synthetic outlet, so it does not establish retry encryption. It also executes the declared-length tolerance.
* `GenerateParity5ChannelRetryCryptoVectors.py` executes the real reference Channel, outlet, Packet, Link, Token and Transport retry path. It captures outbound packets over a synthetic established link, so it is not a handshake or live-node claim. `Parity5ChannelRetryCryptoTests.ActualReferencePacketResendChangesCiphertextAndHashButPreservesEnvelopeAndSequence` validates the capture, and the .NET link retry test checks the matching receipt and proof behaviour.

The xUnit coverage is in `tests\DotReticulum.Transport.Tests\Parity5ChannelTests.cs`, `Parity5ChannelBufferTests.cs`, `Parity5ChannelRetryCryptoTests.cs`, `ChannelTests.cs`, `ChannelResilienceTests.cs` and `LifecycleTests.cs`. It covers envelope encoding, sequence wrap, ordering, RTT and retry behaviour, stream limits, EOF, callbacks and shutdown.

### Live interoperability

Actual pinned Python nodes exercise:

* direct upgraded-MTU Channel traffic, in both endpoint roles
* a Python transport plus .NET transport path with a 40 ms bridge RTT
* selective Channel loss in both directions
* Buffer streams, multipart resources, exhaustion teardown and fresh-link recovery

### Native consumer

`tests\Fixtures\ChannelBufferConsumer` is a separately published consumer of the public `ReticulumNode`, `ChannelBuffer`, `RawChannelReader` and `RawChannelWriter` APIs. It is a test fixture, not a production CLI mode, and its results are Integration evidence rather than a shared Compatibility claim. CI publishes it in Build, records its commit, RID and executable manifest, and reuses that same-run artifact in Test.

Against the import-verified pinned Python peer, the NativeAOT consumer runs:

* **Healthy cases.** Two cases each exchange exactly 340,000 outbound bytes and 200,000 returned bytes. The peer observes both compressed and plain nonempty outbound chunks, and separate compressible and random return streams exercise native decoding.
* **Lossy cases.** These require an actual lost Channel frame in each direction, both return stream kinds and exactly one TCP session.
* **Soak case.** With `DOTRETICULUM_BUFFER_SOAK=1`, a 16 MB outbound and 8 MB return stream run through a 2 Mbit/s, 40 ms RTT bridge, with both losses and exact hashes. Scheduled mesh iterations select it explicitly.

In every case both peers must observe stream EOF, and the consumer requires its channel to be still incomplete and its link still active before teardown, so a disconnect cannot masquerade as EOF. The short healthy and lossy cases have passed on Windows, Linux and macOS in CI, and the soak case has passed in repeated mesh runs apart from the macOS failure listed under [Limitations](#limitations). Run IDs, timings and per-platform results are recorded in [development history](../.github/development-history.md).
