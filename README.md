# Lila Windows Desktop

The .NET 10 WinForms tray app provides the Windows UI and global keyboard
input. The authenticated API, microphone capture, Parakeet ASR, recordings,
transcripts and LilaCore remain in WSL. See the [Island guide](../docs/ISLAND.md),
[vTalks guide](../docs/VTALKS.md), and [Daemon & Operations guide](../docs/DAEMON_AND_OPERATIONS.md)
for discovery, architecture, build/test, installation, shortcuts, troubleshooting,
and verification.

## Startup and WSL

Windows sign-in startup is **off by default**. The user may opt in from Settings;
only the current user's Run key is used, without administrator privileges. The
startup launch hides the conversation window in the tray. A per-user mutex
prevents a duplicate tray process.

While the tray app runs, it owns a tracked WSL process, checks/restarts it,
starts/checks the Linux `lila.service`, and enables keyboard shortcuts only
after authenticated health reports ready. Closing the conversation window hides
the app; **Exit** cancels voice and releases the managed WSL resources. The app
does not enable lingering or change system-wide WSL/network settings.

## Default keyboard bindings

- Right Ctrl: hold-to-talk after the arming delay.
- Pause/Fn+P: toggle-to-talk; first press starts and second press submits.
- Esc: cancel either session without transcript delivery.

Settings allows independent hold/toggle bindings from a conservative whitelist
of single keys and modifier chords. Unsafe or unsupported keys are blocked.
Raw Input runs while focused or in the tray and does not suppress ordinary
keyboard input.

## Island interface

The native WinForms window is a compact floating Island. Drag its header or
avatar to move it; release near a work-area edge to snap it, and use the tuck
control to leave an accessible edge tab. The saved position is clamped to the
current monitor work area after display or DPI changes. Click the avatar to
expand or collapse the conversation panel. Closing the window hides it in the
tray.

Conversation messages use the authenticated Lila API. Approval cards display
Harness state and submit decisions through the existing owner-authentication
flow; a button click alone is never treated as approval. Hidden-Island alerts
use Windows tray notifications.

Voice indicators reflect the local controller and `/voice/status` API. The
current vTalks integration supports an individual capture/transcription/Core
response, not a continuous listen-and-resume session. It also exposes no TTS
playback or audio-level stream, so the Island does not claim a Speaking state
or draw a simulated audio meter. Task/Sentinel event feeds are not currently
available to this client.

## Development commands

Run in PowerShell from this repository root:

```powershell
dotnet build .\windows\Lila.Desktop\Lila.Desktop.csproj -c Release
dotnet test .\windows\Lila.Desktop.Tests\Lila.Desktop.Tests.csproj -c Release
dotnet publish .\windows\Lila.Desktop\Lila.Desktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:PublishTrimmed=false `
  -o "$env:LOCALAPPDATA\LilaDesktop\publish\win-x64"
```

For a complete first install, run `deploy/windows/Setup-Lila.ps1` from
PowerShell at the repository root. It provisions the local mTLS identity,
configures the Windows client and API credential, and installs or safely
restarts the matching WSL user service. It does not enable startup. The
lower-level `deploy/windows/Activate-Lila.ps1` publishes only the desktop app;
`bash deploy/linux/activate-voice-input.sh` can still be used for manual Linux
service activation after mTLS provisioning. See the installation guide for
prerequisites and deactivation details.

Actual device behavior must be recorded separately from automated tests. The
T450s Fn+P/Pause pulse is a known observation; other keyboard and audio hardware
needs its own acceptance run.

## License

Developed and maintained by TicketLabs, part of the Harboria Labs technology portfolio.
Licensed under the GNU Affero General Public License v3.0 (`AGPL-3.0-only`). See [LICENSE](LICENSE) and [NOTICE](NOTICE).
