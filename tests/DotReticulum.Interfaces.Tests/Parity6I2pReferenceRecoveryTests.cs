using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;
using PythonPeer = DotReticulum.Interfaces.Tests.Parity6I2pLiveTests.PythonPeer;

namespace DotReticulum.Interfaces.Tests;

/// <summary>Diagnostic reference counterexample, separate from native bearer acceptance.</summary>
[Collection("Parity6 actual I2P router")]
[Trait("Category", "Integration")]
public sealed class Parity6I2pReferenceRecoveryTests(ITestOutputHelper output)
{
    private static void VerifyReference(JsonElement ready)
    {
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", ready.GetProperty("revision").GetString());
        Assert.Equal("1.5.7", ready.GetProperty("rns_version").GetString());
        Assert.Equal("1.2.1", ready.GetProperty("lxmf_version").GetString());
        Assert.Equal("47bc0fe460e60ca0e9660816441323bcb507e681bc7946866c192b38bd74fb25",
            ready.GetProperty("i2p_git_blob_sha256").GetString());
        Assert.Equal(ready.GetProperty("i2p_git_blob_sha256").GetString(),
            ready.GetProperty("i2p_canonical_lf_sha256").GetString());
    }

    private static async Task<byte[]> KeyHashAsync(string path, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(path, token);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task ExchangeAsync(PythonPeer sender, PythonPeer receiver, JsonElement receiverReady,
        bool receiverInitiator, string tag, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(tag + "-~}");
        var sent = await sender.CommandAsync("send", Convert.ToHexStringLower(payload), token);
        Assert.True(sent.GetProperty("sent").GetBoolean());
        var data = await receiver.EventAsync("data", token);
        Assert.Equal(payload, Convert.FromHexString(data.GetProperty("payload").GetString()!));
        Assert.Equal(receiverReady.GetProperty("dest").GetString(), data.GetProperty("destination").GetString());
        Assert.Equal("I2PInterfacePeer", data.GetProperty("interface_type").GetString());
        Assert.Equal(receiverInitiator, data.GetProperty("initiator").GetBoolean());
        output.WriteLine($"{DateTimeOffset.UtcNow:O} verified callback {tag}");
    }

    [Trait("Category", "Live")]
    [I2pLiveFact]
    public async Task ActualPinnedPythonServerProcessRestartWithSurvivingInitiator()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "TestResults",
            "i2p-reference-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        var phase = "initial pinned Python server";
        try
        {
            var name = Guid.NewGuid().ToString("N");
            var serverDirectory = Path.Combine(directory, "server");
            await using var server = new PythonPeer(output);
            var serverReady = await server.StartAsync(serverDirectory, name, true, null, token,
                staticTransportIdentity: true);
            VerifyReference(serverReady);
            Assert.True(serverReady.GetProperty("static_transport_identity").GetBoolean());
            output.WriteLine("Server verified imports: " + serverReady.GetRawText());
            var serverState = await server.WaitReadyAsync(true, token);
            var address = serverState.GetProperty("b32").GetString();
            var key = Assert.Single(Directory.GetFiles(Path.Combine(serverDirectory, "storage", "i2p"), "*.i2p"));
            var keyHash = await KeyHashAsync(key, token);
            var transportKey = Path.Combine(serverDirectory, "storage", "transport_identity");
            var transportKeyHash = await KeyHashAsync(transportKey, token);
            output.WriteLine($"Persisted I2P filename {Path.GetFileName(key)}; I2P SHA256 {Convert.ToHexStringLower(keyHash)}; transport identity SHA256 {Convert.ToHexStringLower(transportKeyHash)}");
            phase = "initial pinned Python initiator";
            await using var client = new PythonPeer(output);
            var clientReady = await client.StartAsync(Path.Combine(directory, "client"), name, false,
                address, token, reverseAspects: true);
            VerifyReference(clientReady);
            output.WriteLine("Initiator verified imports: " + clientReady.GetRawText());
            Assert.Equal(serverReady.GetProperty("dest").GetString(), clientReady.GetProperty("native_dest").GetString());
            Assert.Equal(clientReady.GetProperty("dest").GetString(), serverReady.GetProperty("native_dest").GetString());
            var clientProcess = client.ProcessId;
            await client.WaitReadyAsync(false, token);
            await server.WaitReadyAsync(false, token);
            await ExchangeAsync(client, server, serverReady, false, "before-client-to-server", token);
            await ExchangeAsync(server, client, clientReady, true, "before-server-to-client", token);
            phase = "server process termination and persisted restart";
            output.WriteLine($"{DateTimeOffset.UtcNow:O} stopping server PID {server.ProcessId}; surviving initiator PID {clientProcess}");
            await server.StopProcessAsync(token);
            await using var replacement = new PythonPeer(output);
            var replacementReady = await replacement.StartAsync(serverDirectory, name, true, null,
                token, resumeDirectory: true, staticTransportIdentity: true);
            VerifyReference(replacementReady);
            Assert.True(replacementReady.GetProperty("static_transport_identity").GetBoolean());
            Assert.Equal(serverReady.GetProperty("transport_identity_hash").GetString(),
                replacementReady.GetProperty("transport_identity_hash").GetString());
            Assert.Equal(transportKeyHash, await KeyHashAsync(transportKey, token));
            var replacementState = await replacement.WaitReadyAsync(true, token);
            Assert.Equal(address, replacementState.GetProperty("b32").GetString());
            Assert.Equal(keyHash, await KeyHashAsync(key, token));
            Assert.Equal(Path.GetFileName(key),
                Path.GetFileName(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(key)!, "*.i2p"))));
            Assert.Equal(serverReady.GetProperty("dest").GetString(), replacementReady.GetProperty("dest").GetString());
            phase = "accepted Python stream after persisted server process restart";
            output.WriteLine($"{DateTimeOffset.UtcNow:O} persisted key/address verified; awaiting replacement accepted peer");
            await replacement.WaitReadyAsync(false, token);
            await client.WaitReadyAsync(false, token);
            Assert.Equal(clientProcess, client.ProcessId);
            await ExchangeAsync(client, replacement, replacementReady, false, "after-client-to-server", token);
            await ExchangeAsync(replacement, client, clientReady, true, "after-server-to-client", token);
            output.WriteLine("PASS diagnostic: pinned Python server PROCESS restart, persisted destination, surviving Python initiator, callbacks both directions before/after. Not native recovery acceptance.");
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            throw new TimeoutException($"Pinned Python restart diagnostic exceeded unchanged four-minute deadline during {phase}.", error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
