using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Teleporter.Connection;

/// <summary>Everything ssh needs to reach the server account.</summary>
public sealed record SshTarget(string Host, string User, int Port, string SshPath, string? IdentityAgent, DateTimeOffset? ExpiresAt);

/// <summary>
/// How Teleporter gets credentials for a server. The public app ships two: plain
/// OpenSSH (your own agent and keys) and an external command, which is how private
/// setups (a key held in an encrypted vault, for example) plug in without their logic
/// living in this repo. See the README, "Connection providers".
/// </summary>
public interface IConnectionProvider
{
    Task<SshTarget> PrepareAsync(CancellationToken ct);
    Task ReleaseAsync();
}

internal sealed class OpenSshProvider(ServerProfile profile, string sshPath) : IConnectionProvider
{
    public Task<SshTarget> PrepareAsync(CancellationToken ct) =>
        Task.FromResult(new SshTarget(profile.Host, profile.User, profile.Port, sshPath, null, null));

    public Task ReleaseAsync() => Task.CompletedTask;
}

internal sealed class ExternalCommandProvider(ServerProfile profile, string defaultSshPath) : IConnectionProvider
{
    private SshTarget? _cached;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SshTarget> PrepareAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && (_cached.ExpiresAt is null || _cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)))
                return _cached;

            if (string.IsNullOrWhiteSpace(profile.ProviderCommand))
                throw new InvalidOperationException("No provider command is set for this server.");

            var (file, args) = ProcessRunner.ResolveScript(profile.ProviderCommand);
            args.Add("prepare");
            var r = await ProcessRunner.RunAsync(file, args, timeout: TimeSpan.FromSeconds(60), ct: ct);
            if (!r.Ok)
                throw new InvalidOperationException($"Provider failed (exit {r.ExitCode}): {r.StdErr.Trim()}");

            // The provider may print progress first; the JSON object is the last line.
            string json = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'))
                          ?? throw new InvalidOperationException("Provider printed no JSON object.");
            var reply = JsonSerializer.Deserialize(json, ProviderJsonContext.Default.ProviderReply)
                        ?? throw new InvalidOperationException("Provider JSON was empty.");

            _cached = new SshTarget(
                reply.Host ?? profile.Host,
                reply.User ?? profile.User,
                reply.Port ?? profile.Port,
                reply.SshPath ?? defaultSshPath,
                reply.IdentityAgent,
                reply.ExpiresAt);
            Log.Info($"provider prepared {_cached.User}@{_cached.Host}");
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync()
    {
        if (_cached is null || string.IsNullOrWhiteSpace(profile.ProviderCommand)) return;
        try
        {
            var (file, args) = ProcessRunner.ResolveScript(profile.ProviderCommand);
            args.Add("release");
            await ProcessRunner.RunAsync(file, args, timeout: TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            Log.Warn($"provider release failed: {ex.Message}");
        }
        _cached = null;
    }
}

internal sealed class ProviderReply
{
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("sshPath")] public string? SshPath { get; set; }
    [JsonPropertyName("identityAgent")] public string? IdentityAgent { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
}

[JsonSerializable(typeof(ProviderReply))]
internal partial class ProviderJsonContext : JsonSerializerContext
{
}

internal static class ConnectionFactory
{
    public static IConnectionProvider For(ServerProfile profile, AppSettings settings) =>
        profile.Provider == "external"
            ? new ExternalCommandProvider(profile, settings.SshPath)
            : new OpenSshProvider(profile, settings.SshPath);
}
