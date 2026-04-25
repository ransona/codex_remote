# codex_remote Protocol

`codex_remote` is a JSON-over-UDP protocol for consented remote shell execution.

Every packet includes:

```json
{
  "protocol": "codex_remote",
  "version": "1",
  "type": "..."
}
```

## Flow

1. Client sends `hello`.
2. Listener prompts the local user to allow or reject the session.
3. Listener returns `hello_response`.
4. Client sends `command_request` with the approved session id and token.
5. Listener streams stdout/stderr via `stream_chunk`.
6. Client acknowledges each streamed packet with `ack`.
7. Listener sends `command_complete`.
8. Client acknowledges the completion packet with `ack`.

## Packet Types

### `hello`

```json
{
  "protocol": "codex_remote",
  "version": "1",
  "type": "hello",
  "requestId": "7f6e...",
  "operatorName": "codex",
  "host": "operator-hostname"
}
```

### `hello_response`

```json
{
  "protocol": "codex_remote",
  "version": "1",
  "type": "hello_response",
  "requestId": "7f6e...",
  "approved": true,
  "message": "Approved.",
  "sessionId": "4a8d...",
  "sessionToken": "d20c..."
}
```

If rejected, `approved` is `false` and `sessionId` and `sessionToken` are omitted.

### `command_request`

```json
{
  "protocol": "codex_remote",
  "version": "1",
  "type": "command_request",
  "requestId": "ab12...",
  "sessionId": "4a8d...",
  "sessionToken": "d20c...",
  "shell": "powershell",
  "command": "hostname",
  "workingDirectory": "C:\\\\",
  "timeoutMs": 30000
}
```

Supported shell values in this implementation:

- `powershell`
- `pwsh`
- `cmd`

### `stream_chunk`

```json
{
  "protocol": "codex_remote",
  "version": "1",
  "type": "stream_chunk",
  "requestId": "ab12...",
  "sessionId": "4a8d...",
  "stream": "stdout",
  "payloadBase64": "aGVsbG8NCg==",
  "ackKey": "f190..."
}
```

`stream` is either `stdout` or `stderr`.

`payloadBase64` contains UTF-8 text. The current implementation streams line-by-line and includes trailing newlines.

### `command_complete`

```json
{
  "protocol": "codex_remote",
  "version": "1",
  "type": "command_complete",
  "requestId": "ab12...",
  "sessionId": "4a8d...",
  "exitCode": 0,
  "message": null,
  "ackKey": "0a44..."
}
```

If the command timed out, `exitCode` is `-1` and `message` may explain the timeout.

### `ack`

```json
{
  "protocol": "codex_remote",
  "version": "1",
  "type": "ack",
  "requestId": "ab12...",
  "ackKey": "f190..."
}
```

The sender retries if no matching acknowledgement arrives.

### `error`

```json
{
  "protocol": "codex_remote",
  "version": "1",
  "type": "error",
  "requestId": "ab12...",
  "message": "Unknown session."
}
```

## Operational Notes

- Session approval is interactive and local to the listening machine.
- The server currently binds an approved session to the client IP address plus the session token.
- The implementation favors simple reliable streaming over maximum throughput.
- This is not encrypted and should be treated as trusted-network tooling, not an internet-safe remote shell.
