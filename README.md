# codex_remote

`codex_remote` is a .NET 8 command-line app for consent-based remote shell execution over UDP.

The remote machine runs a foreground listener. When a client asks to connect, the listener prompts the local user to approve or reject the session. Once approved, the client can execute shell commands and receive streamed stdout/stderr in near real time.

## Build

```powershell
dotnet build
```

## Publish For Normal Windows Machines

To produce a standalone `.exe` that does not require .NET to be installed on the target machine:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

This publishes a self-contained Windows build to:

[dist/win-x64](C:\code\repos\codex_remote\dist\win-x64)

The main executable will be:

[dist/win-x64/CodexRemote.exe](C:\code\repos\codex_remote\dist\win-x64\CodexRemote.exe)

To also build an ARM64 package:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Runtime win-x64,win-arm64
```

To build zip archives too:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Zip
```

## Run

Start the listener on the remote machine:

```powershell
dotnet run -- listen --port 45821
```

If the remote machine does not have the .NET SDK, run the published executable instead:

```powershell
.\dist\win-x64\CodexRemote.exe listen --port 45821
```

Approve the session from the client machine:

```powershell
dotnet run -- connect --host 192.168.1.10 --name codex
```

Run one command:

```powershell
dotnet run -- run --host 192.168.1.10 --command "hostname"
```

Open an interactive session:

```powershell
dotnet run -- shell --host 192.168.1.10
```

Using the published executable:

```powershell
.\dist\win-x64\CodexRemote.exe connect --host 192.168.1.10 --name codex
.\dist\win-x64\CodexRemote.exe run --host 192.168.1.10 --command "hostname"
.\dist\win-x64\CodexRemote.exe shell --host 192.168.1.10
```

List stored sessions:

```powershell
dotnet run -- sessions list
```

Remove a stored session:

```powershell
dotnet run -- sessions remove --host 192.168.1.10
```

## Commands

- `listen`: bind a UDP listener and prompt for approval on incoming connection requests.
- `connect`: request a session and store the returned session id and token in `%USERPROFILE%\\.codex_remote\\sessions.json`.
- `run`: execute one remote shell command using a stored approved session.
- `shell`: repeatedly execute commands against the same approved remote host.
- `sessions`: inspect or remove locally stored sessions.

## Protocol

The protocol name is `codex_remote`.

- `hello`: client asks for a session.
- `hello_response`: server returns approval status and, if approved, a session id and token.
- `command_request`: client sends the shell, command text, cwd, and timeout.
- `stream_chunk`: server streams stdout or stderr text while the process is still running.
- `command_complete`: server sends the final exit code when the process ends.
- `ack`: receiver acknowledges streamed packets so the sender can retry on UDP loss.
- `error`: server returns a request-scoped failure.

See [docs/codex_remote_protocol.md](C:\code\repos\codex_remote\docs\codex_remote_protocol.md) for the wire-level shape.

## Will This Work?

Yes, for machines you control on a trusted network, provided the listener is already running and the user on that machine explicitly approves the session.

No, not as a hardened remote admin product for the public internet. Important limits:

- UDP is lossy and unordered, so this implementation uses packet acknowledgements and retries but is still best on stable LAN or VPN links.
- Traffic is not encrypted.
- The approval prompt requires an interactive console on the remote machine.
- The session token authenticates later requests, but the design is still much weaker than SSH, WinRM, or Tailscale plus SSH.

If you want this to be dependable across the internet, the better design is:

1. Keep the approval UX.
2. Replace raw UDP command transport with TLS over TCP or WebSockets.
3. Add mutual authentication.
4. Consider a brokered relay only if direct connectivity is not possible.
