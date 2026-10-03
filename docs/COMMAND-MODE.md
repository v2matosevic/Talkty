# Optional local command integration

Command mode is separate from ordinary dictation. It is off by default and requires
a compatible local service, which is **not included in the Talkty installer or this
repository**. Building and using normal dictation requires no command service.

Enable it under Settings > Hotkey, set the local endpoint/token and command shortcut
(default Alt+W). Alt+Q remains ordinary dictation. A command is transcribed with the
selected local/cloud model, then sent to the configured service. Its words, progress
and result appear on the pill. Local models can show provisional command words while
recording. Commands bypass Prompting and never fall back to typing into another app.

## Service discovery and authentication

Talkty first checks `%LOCALAPPDATA%/Version2/services/hermes-voice.json`. The record must
name service `hermes-voice`, contain a live `pid`, and resolve to a loopback URL. `url`
is the base address; optional `command` defaults to `/command`. Optional `token` supplies
the shared request token. Do not commit service records or real tokens.

Without a valid live record, Talkty uses the configured endpoint only after `GET /health`
identifies the service with `{"service":"hermes-voice"}`. Remote endpoints are refused.
If an existing Hermes installation created the `Hermes voice daemon.vbs` Startup entry,
Talkty can request that service start. Talkty does not download or install the service.

## Request contract

`POST` the command endpoint with JSON and the `x-voice-token` header. The payload contains
`operationId`, `text`, `hotkey` (`command`), `foregroundApp`, `sentAt` (Unix milliseconds), and optional
`targetWindow` with `handle`, `pid`, `processName`, `title` and process `startedAt`.
The target is captured before transcription so later window changes do not retarget it.

The service owns action execution and authorization. Implementers should follow the
current [client/parser](../Talkty.App/Services/VoiceCommandService.cs) and
[contract tests](../Talkty.Tests/VoiceCommandServiceTests.cs), including uncertainty,
confirmation and goal-status responses. An interrupted request may already have arrived;
Talkty does not blindly send it again. When a response includes a running goal, the pill
can follow the service's `/goals/<id>` status endpoint with the same token.

After a lost response, connection failure or server error, Talkty queries
`GET /operations/<operationId>` with the same token. A usable receipt must match
the operation ID and include `deliveryState`; an absent, mismatched or unreadable
receipt remains uncertain and does not trigger another command. Durable receipts
require the matching Hermes operation-receipt update. The service must claim the
operation before executing it, reject changed payloads for a reused ID and retain
the receipt across restart. Older services can still execute ordinary commands,
but cannot establish this durable reconciliation without the update.

## Privacy and boundaries

The local service receives the transcript and captured window information. It may use
its own models, tools or network services; its behavior is outside Talkty's offline
transcription guarantee. A cloud transcription model still sends audio to OpenRouter
before dispatch. Select a local model and disable command mode for ordinary offline
dictation. The integration does not imply that Talkty provides a general desktop agent.
