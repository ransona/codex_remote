using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var exitCode = await App.RunAsync(args);
Environment.Exit(exitCode);

static class Constants
{
    public const int DefaultPort = 45821;
    public const int DefaultTimeoutMs = 30000;
    public const int MaxDatagramBytes = 1100;
    public const int AckTimeoutMs = 1500;
    public const int AckRetries = 8;
    public const string ProtocolName = "codex_remote";
    public const string ProtocolVersion = "1";
}

static class App
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || HasHelp(args))
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var tail = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "listen" => await RunListenAsync(tail),
                "connect" => await RunConnectAsync(tail),
                "run" => await RunRemoteCommandAsync(tail),
                "shell" => await RunInteractiveShellAsync(tail),
                "sessions" => await RunSessionsAsync(tail),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (OperationCanceledException)
        {
            return Fail("Operation cancelled.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static bool HasHelp(IEnumerable<string> args) =>
        args.Any(arg => arg is "-h" or "--help" or "help");

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            codex_remote

            Commands:
              listen  Start a UDP listener that prompts for remote-control approval.
              connect Request approval from a remote listener and store the session.
              run     Execute one remote shell command over an approved session.
              shell   Open an interactive remote shell over an approved session.
              sessions List or remove stored sessions.

            Examples:
              CodexRemote listen --port 45821
              CodexRemote connect --host 192.168.1.10 --name codex
              CodexRemote run --host 192.168.1.10 --command "hostname"
              CodexRemote shell --host 192.168.1.10
            """);
    }

    private static async Task<int> RunListenAsync(string[] args)
    {
        var bind = GetOption(args, "--bind") ?? "0.0.0.0";
        var port = GetIntOption(args, "--port", Constants.DefaultPort);
        var listener = new Listener(bind, port);
        Console.WriteLine($"Listening on udp://{bind}:{port} using protocol '{Constants.ProtocolName}'.");
        Console.WriteLine("Press Ctrl+C to stop.");
        await listener.RunAsync();
        return 0;
    }

    private static async Task<int> RunConnectAsync(string[] args)
    {
        var host = RequireOption(args, "--host");
        var port = GetIntOption(args, "--port", Constants.DefaultPort);
        var operatorName = GetOption(args, "--name") ?? Environment.MachineName;
        var store = SessionStore.Load();

        using var client = new UdpClient(0);
        var remoteEndpoint = new IPEndPoint(await ResolveHostAsync(host), port);
        var requestId = IdGenerator.NewId();
        var helloPacket = new Packet
        {
            Type = PacketType.Hello,
            RequestId = requestId,
            OperatorName = operatorName,
            Host = Dns.GetHostName()
        };

        var response = await Protocol.SendRequestAsync<Packet>(
            client,
            remoteEndpoint,
            helloPacket,
            expectedType: PacketType.HelloResponse,
            timeoutMs: 120000);

        if (!response.Approved || string.IsNullOrWhiteSpace(response.SessionId) || string.IsNullOrWhiteSpace(response.SessionToken))
        {
            return Fail(response.Message ?? "Remote host denied the session.");
        }

        var session = new StoredSession
        {
            Host = host,
            Port = port,
            SessionId = response.SessionId,
            SessionToken = response.SessionToken,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            OperatorName = operatorName
        };

        store.Upsert(session);
        await store.SaveAsync();

        Console.WriteLine($"Session approved for {host}:{port}.");
        Console.WriteLine($"Session id: {session.SessionId}");
        return 0;
    }

    private static async Task<int> RunRemoteCommandAsync(string[] args)
    {
        var command = GetCommandText(args);
        if (string.IsNullOrWhiteSpace(command))
        {
            return Fail("Missing command. Use --command \"...\" or pass the command after --.");
        }

        var host = RequireOption(args, "--host");
        var port = GetIntOption(args, "--port", Constants.DefaultPort);
        var shell = GetOption(args, "--shell") ?? "powershell";
        var cwd = GetOption(args, "--cwd");
        var timeoutMs = GetIntOption(args, "--timeout-ms", Constants.DefaultTimeoutMs);

        var exitCode = await RemoteExecutor.ExecuteAsync(host, port, shell, command, cwd, timeoutMs);
        Console.Error.WriteLine($"[remote exit code: {exitCode}]");
        return exitCode;
    }

    private static async Task<int> RunInteractiveShellAsync(string[] args)
    {
        var host = RequireOption(args, "--host");
        var port = GetIntOption(args, "--port", Constants.DefaultPort);
        var shell = GetOption(args, "--shell") ?? "powershell";
        var cwd = GetOption(args, "--cwd");
        var timeoutMs = GetIntOption(args, "--timeout-ms", Constants.DefaultTimeoutMs);

        Console.WriteLine($"Interactive remote shell on {host}:{port} using {shell}. Type 'exit' to quit.");
        while (true)
        {
            Console.Write("remote> ");
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var exitCode = await RemoteExecutor.ExecuteAsync(host, port, shell, line, cwd, timeoutMs);
            Console.Error.WriteLine($"[remote exit code: {exitCode}]");
        }

        return 0;
    }

    private static async Task<int> RunSessionsAsync(string[] args)
    {
        var subcommand = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        var store = SessionStore.Load();

        if (subcommand == "list")
        {
            foreach (var session in store.Sessions.OrderBy(s => s.Host).ThenBy(s => s.Port))
            {
                Console.WriteLine($"{session.Host}:{session.Port} session={session.SessionId} approved={session.ApprovedAtUtc:O}");
            }

            if (store.Sessions.Count == 0)
            {
                Console.WriteLine("No stored sessions.");
            }

            return 0;
        }

        if (subcommand == "remove")
        {
            var host = RequireOption(args.Skip(1).ToArray(), "--host");
            var port = GetIntOption(args.Skip(1).ToArray(), "--port", Constants.DefaultPort);
            if (!store.Remove(host, port))
            {
                return Fail("No stored session matched that host and port.");
            }

            await store.SaveAsync();
            Console.WriteLine($"Removed session for {host}:{port}.");
            return 0;
        }

        return Fail($"Unknown sessions subcommand '{subcommand}'.");
    }

    private static string? GetCommandText(string[] args)
    {
        var commandOption = GetOption(args, "--command");
        if (!string.IsNullOrWhiteSpace(commandOption))
        {
            return commandOption;
        }

        var separatorIndex = Array.IndexOf(args, "--");
        if (separatorIndex >= 0 && separatorIndex < args.Length - 1)
        {
            return string.Join(" ", args.Skip(separatorIndex + 1));
        }

        return null;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new InvalidOperationException($"Missing value for {name}.");
            }

            return args[i + 1];
        }

        return null;
    }

    private static string RequireOption(string[] args, string name) =>
        GetOption(args, name) ?? throw new InvalidOperationException($"Missing required option {name}.");

    private static int GetIntOption(string[] args, string name, int fallback)
    {
        var value = GetOption(args, name);
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException($"Invalid integer value for {name}: {value}");
        }

        return parsed;
    }

    private static async Task<IPAddress> ResolveHostAsync(string host)
    {
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            return ipAddress;
        }

        var addresses = await Dns.GetHostAddressesAsync(host);
        var match = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? addresses.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Could not resolve host '{host}'.");
        return match;
    }
}

sealed class Listener
{
    private readonly UdpClient _udpClient;
    private readonly IPEndPoint _listenEndpoint;
    private readonly ConcurrentDictionary<string, ApprovedSession> _sessions = new();
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    public Listener(string bind, int port)
    {
        var address = bind == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(bind);
        _listenEndpoint = new IPEndPoint(address, port);
        _udpClient = new UdpClient(_listenEndpoint);
    }

    public async Task RunAsync()
    {
        while (true)
        {
            var received = await _udpClient.ReceiveAsync();
            _ = Task.Run(() => HandleAsync(received));
        }
    }

    private async Task HandleAsync(UdpReceiveResult received)
    {
        Packet? packet;
        try
        {
            packet = Protocol.Deserialize(received.Buffer);
        }
        catch
        {
            return;
        }

        if (packet?.Version != Constants.ProtocolVersion)
        {
            return;
        }

        switch (packet.Type)
        {
            case PacketType.Hello:
                await HandleHelloAsync(packet, received.RemoteEndPoint);
                break;
            case PacketType.CommandRequest:
                await HandleCommandAsync(packet, received.RemoteEndPoint);
                break;
        }
    }

    private async Task HandleHelloAsync(Packet hello, IPEndPoint remoteEndPoint)
    {
        await _promptGate.WaitAsync();
        try
        {
            Console.WriteLine();
            Console.WriteLine($"Remote control request from {remoteEndPoint.Address}:{remoteEndPoint.Port}");
            Console.WriteLine($"Operator: {hello.OperatorName ?? "unknown"}");
            Console.WriteLine($"Source host hint: {hello.Host ?? "unknown"}");
            Console.Write("Allow remote commands for this session? [y/N]: ");
            var answer = Console.ReadLine()?.Trim();
            var approved = string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);

            var response = new Packet
            {
                Type = PacketType.HelloResponse,
                RequestId = hello.RequestId,
                Approved = approved,
                Message = approved ? "Approved." : "Rejected by user."
            };

            if (approved)
            {
                var session = ApprovedSession.Create(remoteEndPoint);
                _sessions[session.SessionId] = session;
                response.SessionId = session.SessionId;
                response.SessionToken = session.SessionToken;
            }

            await Protocol.SendPacketAsync(_udpClient, remoteEndPoint, response);
        }
        finally
        {
            _promptGate.Release();
        }
    }

    private async Task HandleCommandAsync(Packet commandPacket, IPEndPoint remoteEndPoint)
    {
        if (string.IsNullOrWhiteSpace(commandPacket.SessionId) ||
            string.IsNullOrWhiteSpace(commandPacket.SessionToken) ||
            !_sessions.TryGetValue(commandPacket.SessionId, out var session))
        {
            await Protocol.SendPacketAsync(_udpClient, remoteEndPoint, Packet.Error(commandPacket.RequestId, "Unknown session."));
            return;
        }

        if (!session.IsMatch(remoteEndPoint, commandPacket.SessionToken))
        {
            await Protocol.SendPacketAsync(_udpClient, remoteEndPoint, Packet.Error(commandPacket.RequestId, "Session token or source endpoint mismatch."));
            return;
        }

        var processResult = await CommandRunner.RunStreamingAsync(
            commandPacket.Command ?? string.Empty,
            commandPacket.Shell ?? "powershell",
            commandPacket.WorkingDirectory,
            commandPacket.TimeoutMs.GetValueOrDefault(Constants.DefaultTimeoutMs),
            async streamItem =>
            {
                var streamPacket = new Packet
                {
                    Type = PacketType.StreamChunk,
                    RequestId = commandPacket.RequestId,
                    SessionId = session.SessionId,
                    Stream = streamItem.Stream,
                    PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(streamItem.Text))
                };

                await Protocol.SendWithAckAsync(_udpClient, remoteEndPoint, streamPacket, commandPacket.RequestId!, null);
            });

        var completionPacket = new Packet
        {
            Type = PacketType.CommandComplete,
            RequestId = commandPacket.RequestId,
            SessionId = session.SessionId,
            ExitCode = processResult.ExitCode,
            Message = processResult.TimedOut ? "Command timed out." : null
        };
        await Protocol.SendWithAckAsync(_udpClient, remoteEndPoint, completionPacket, commandPacket.RequestId!, -1);
    }
}

static class RemoteExecutor
{
    public static async Task<int> ExecuteAsync(string host, int port, string shell, string command, string? cwd, int timeoutMs)
    {
        var store = SessionStore.Load();
        var session = store.Get(host, port)
                      ?? throw new InvalidOperationException($"No stored session for {host}:{port}. Run connect first.");
        var remoteIp = IPAddress.TryParse(host, out var ip) ? ip : await Dns.GetHostAddressesAsync(host).ContinueWith(t => t.Result[0]);
        var remoteEndpoint = new IPEndPoint(remoteIp, port);

        using var client = new UdpClient(0);
        var requestId = IdGenerator.NewId();
        var requestPacket = new Packet
        {
            Type = PacketType.CommandRequest,
            RequestId = requestId,
            SessionId = session.SessionId,
            SessionToken = session.SessionToken,
            Shell = shell,
            Command = command,
            WorkingDirectory = cwd,
            TimeoutMs = timeoutMs
        };

        await Protocol.SendPacketAsync(client, remoteEndpoint, requestPacket);

        using var cts = new CancellationTokenSource(timeoutMs + 15000);
        while (!cts.IsCancellationRequested)
        {
            var packet = await Protocol.ReceivePacketAsync(client, cts.Token);
            if (packet.Type == PacketType.Error && packet.RequestId == requestId)
            {
                throw new InvalidOperationException(packet.Message ?? "Remote error.");
            }

            if (packet.RequestId != requestId)
            {
                continue;
            }

            if (packet.Type == PacketType.StreamChunk)
            {
                var bytes = string.IsNullOrEmpty(packet.PayloadBase64)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(packet.PayloadBase64);
                var text = Encoding.UTF8.GetString(bytes);
                if (packet.Stream == "stderr")
                {
                    Console.Error.Write(text);
                }
                else
                {
                    Console.Write(text);
                }

                var streamAck = new Packet
                {
                    Type = PacketType.Ack,
                    RequestId = requestId,
                    AckKey = packet.AckKey
                };
                await Protocol.SendPacketAsync(client, remoteEndpoint, streamAck);
                continue;
            }

            if (packet.Type == PacketType.CommandComplete)
            {
                var completionAck = new Packet
                {
                    Type = PacketType.Ack,
                    RequestId = requestId,
                    AckKey = packet.AckKey
                };
                await Protocol.SendPacketAsync(client, remoteEndpoint, completionAck);
                return packet.ExitCode ?? -1;
            }
        }

        throw new TimeoutException("Timed out receiving the remote command result.");
    }
}

static class CommandRunner
{
    public static async Task<CommandExecution> RunStreamingAsync(
        string command,
        string shell,
        string? cwd,
        int timeoutMs,
        Func<StreamItem, Task> onOutputAsync)
    {
        var startInfo = BuildStartInfo(command, shell, cwd);
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = PumpStreamAsync(process.StandardOutput, "stdout", onOutputAsync);
        var stderrTask = PumpStreamAsync(process.StandardError, "stderr", onOutputAsync);

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            await onOutputAsync(new StreamItem("stderr", $"{Environment.NewLine}Command timed out after {timeoutMs} ms.{Environment.NewLine}"));
            return new CommandExecution { ExitCode = -1, TimedOut = true };
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new CommandExecution { ExitCode = process.ExitCode };
    }

    private static async Task PumpStreamAsync(StreamReader reader, string streamName, Func<StreamItem, Task> onOutputAsync)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            await onOutputAsync(new StreamItem(streamName, line + Environment.NewLine));
        }
    }

    private static ProcessStartInfo BuildStartInfo(string command, string shell, string? cwd)
    {
        var shellLower = shell.ToLowerInvariant();
        string fileName;
        string arguments;

        switch (shellLower)
        {
            case "cmd":
                fileName = "cmd.exe";
                arguments = $"/c {command}";
                break;
            case "powershell":
            case "pwsh":
                fileName = shellLower == "pwsh" ? "pwsh.exe" : "powershell.exe";
                arguments = $"-NoLogo -NoProfile -NonInteractive -Command {command}";
                break;
            default:
                throw new InvalidOperationException($"Unsupported shell '{shell}'. Use powershell, pwsh, or cmd.");
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd
        };
    }
}

static class Protocol
{
    public static async Task SendPacketAsync(UdpClient client, IPEndPoint endpoint, Packet packet)
    {
        var bytes = Serialize(packet);
        await client.SendAsync(bytes, bytes.Length, endpoint);
    }

    public static async Task<TPacket> SendRequestAsync<TPacket>(UdpClient client, IPEndPoint endpoint, Packet packet, string expectedType, int timeoutMs)
        where TPacket : Packet
    {
        await SendPacketAsync(client, endpoint, packet);
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!cts.IsCancellationRequested)
        {
            var response = await ReceivePacketAsync(client, cts.Token);
            if (response.Type == expectedType && response.RequestId == packet.RequestId)
            {
                return response as TPacket ?? throw new InvalidOperationException("Unexpected response type.");
            }
        }

        throw new TimeoutException("Timed out waiting for remote response.");
    }

    public static async Task<Packet> ReceivePacketAsync(UdpClient client, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(client.Dispose);
        try
        {
            var received = await client.ReceiveAsync();
            return Deserialize(received.Buffer) ?? throw new InvalidOperationException("Received invalid packet.");
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public static async Task SendWithAckAsync(UdpClient client, IPEndPoint endpoint, Packet packet, string requestId, int? sequence)
    {
        var ackKey = packet.AckKey ?? IdGenerator.NewId();
        packet.AckKey = ackKey;
        for (var attempt = 0; attempt < Constants.AckRetries; attempt++)
        {
            await SendPacketAsync(client, endpoint, packet);
            using var cts = new CancellationTokenSource(Constants.AckTimeoutMs);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var response = await ReceivePacketAsync(client, cts.Token);
                    if (response.Type == PacketType.Ack && response.RequestId == requestId && response.AckKey == ackKey)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        throw new TimeoutException($"Timed out waiting for ack for packet {ackKey}.");
    }

    public static byte[] Serialize(Packet packet) => JsonSerializer.SerializeToUtf8Bytes(packet);

    public static Packet? Deserialize(byte[] buffer) => JsonSerializer.Deserialize<Packet>(buffer);

    public static IEnumerable<byte[]> Chunk(byte[] payload, int chunkSize)
    {
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, payload.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(payload, offset, chunk, 0, length);
            yield return chunk;
        }
    }
}

static class IdGenerator
{
    public static string NewId()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }
}

sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public List<StoredSession> Sessions { get; init; } = [];

    public static SessionStore Load()
    {
        var path = GetPath();
        if (!File.Exists(path))
        {
            return new SessionStore();
        }

        return JsonSerializer.Deserialize<SessionStore>(File.ReadAllText(path), JsonOptions) ?? new SessionStore();
    }

    public async Task SaveAsync()
    {
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void Upsert(StoredSession session)
    {
        Sessions.RemoveAll(existing =>
            existing.Host.Equals(session.Host, StringComparison.OrdinalIgnoreCase) &&
            existing.Port == session.Port);
        Sessions.Add(session);
    }

    public StoredSession? Get(string host, int port) =>
        Sessions.LastOrDefault(session =>
            session.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
            session.Port == port);

    public bool Remove(string host, int port) =>
        Sessions.RemoveAll(session =>
            session.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
            session.Port == port) > 0;

    private static string GetPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex_remote", "sessions.json");
}

sealed class ApprovedSession
{
    public required string SessionId { get; init; }
    public required string SessionToken { get; init; }
    public required string EndpointKey { get; init; }

    public static ApprovedSession Create(IPEndPoint endpoint) => new()
    {
        SessionId = IdGenerator.NewId(),
        SessionToken = IdGenerator.NewId(),
        EndpointKey = ToKey(endpoint)
    };

    public bool IsMatch(IPEndPoint endpoint, string token) =>
        SessionToken == token && EndpointKey == ToKey(endpoint);

    private static string ToKey(IPEndPoint endpoint) => endpoint.Address.ToString();
}

sealed class StoredSession
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string SessionId { get; init; }
    public required string SessionToken { get; init; }
    public required DateTimeOffset ApprovedAtUtc { get; init; }
    public required string OperatorName { get; init; }
}

sealed class CommandExecution
{
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
}

static class PacketType
{
    public const string Hello = "hello";
    public const string HelloResponse = "hello_response";
    public const string CommandRequest = "command_request";
    public const string StreamChunk = "stream_chunk";
    public const string CommandComplete = "command_complete";
    public const string Ack = "ack";
    public const string Error = "error";
}

class Packet
{
    public string Protocol { get; set; } = Constants.ProtocolName;
    public string Version { get; set; } = Constants.ProtocolVersion;
    public string Type { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string? Host { get; set; }
    public string? OperatorName { get; set; }
    public bool Approved { get; set; }
    public string? Message { get; set; }
    public string? SessionId { get; set; }
    public string? SessionToken { get; set; }
    public string? Shell { get; set; }
    public string? Command { get; set; }
    public string? WorkingDirectory { get; set; }
    public int? TimeoutMs { get; set; }
    public int? Sequence { get; set; }
    public int? Total { get; set; }
    public string? Stream { get; set; }
    public int? ExitCode { get; set; }
    public string? PayloadBase64 { get; set; }
    public string? AckKey { get; set; }

    public static Packet Error(string? requestId, string message) => new()
    {
        Type = PacketType.Error,
        RequestId = requestId,
        Message = message
    };
}

readonly record struct StreamItem(string Stream, string Text);
