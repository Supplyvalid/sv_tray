# Shelivo Print Agent

A small Windows background service that lets the Shelivo POS **running in a
plain browser** print raw ESC/POS commands and open the cash drawer on the
printer wired to that till.

It listens on `http://127.0.0.1:9200` and does nothing else. The POS web app
calls it to list printers and send raw byte sequences.

## Do you actually need this?

**Probably not, if the till runs the packaged Shelivo desktop app.** That app
talks to the printer and cash drawer natively — silent printing, no dialog,
no agent, nothing extra to install.

This agent exists only for tills that open the POS in a normal browser tab,
where the page is not allowed to touch hardware directly. Note that even
with the agent installed, a browser till **still shows the browser's print
dialog** for receipts — the agent only handles raw commands such as the
cash-drawer kick. Only the desktop app prints silently.

## Install on a till

Run `ShelivoPrintAgentSetup.exe` once. It:

- installs to `%LocalAppData%\Programs\Shelivo Print Agent\` (per-user, **no
  admin/UAC prompt**),
- registers "Shelivo Print Agent" in Windows' Installed Apps list, with a
  working Uninstall entry,
- registers autostart so it launches hidden at every logon,
- starts it immediately.

Nothing else to run, and cashiers never interact with it. It is part of till
provisioning, alongside installing the printer driver.

Since it isn't code-signed, Windows may show a **"Windows protected your PC"**
screen on first run — click **More info → Run anyway**. A code-signing
certificate would remove that; see "Code signing" below.

Set `PRINT_AGENT_ALLOWED_ORIGINS` to the real production POS URL on tills
that use it (see Configuration).

## Build

```powershell
.\build.ps1
```

Produces in `dist\`:
- `shelivo-print-agent.exe` — the agent itself (self-contained; no .NET
  install needed on the till),
- `ShelivoPrintAgentSetup.exe` — the installer, which is what goes to tills.

Requires the .NET 8 SDK, plus NSIS (`makensis.exe`) for the installer step.
`build.ps1` finds NSIS automatically if it's on PATH or if electron-builder
has cached a copy on this machine; otherwise pass `-MakeNsis <path>` or use
`-SkipInstaller`.

To run it directly during development:

```powershell
dotnet run --project src\ShelivoPrintAgent.csproj
```

## Configuration (environment variables)

- `PRINT_AGENT_PORT` — default `9200`.
- `PRINT_AGENT_ALLOWED_ORIGINS` — comma-separated origins allowed to call the
  agent, default `http://localhost:4200`. **Set this to the real production
  POS URL** on production tills; the default only covers local dev.

## API

- `GET /health` → `{ ok: true, version }`
- `GET /printers` → `["IHR810", "Microsoft Print to PDF", ...]`
- `POST /print` with `{ "printer": "IHR810", "sequence": "27,112,0,25,250" }`
  sends those raw bytes to that printer.

## How raw printing works

`src/RawPrinter.cs` tries the gdi32 `PASSTHROUGH` escape first, then falls
back to the classic `winspool.drv` `WritePrinter`/RAW-datatype recipe.
Different printer drivers support only one or the other — confirmed against
a real Honeywell Impact/IHR810, where `WritePrinter` alone fails with
`ERROR_INVALID_DATATYPE` while `PASSTHROUGH` succeeds. This is native
P/Invoke, so there's no PowerShell subprocess in the print path.

## Browser notes

**Private Network Access.** A production POS URL is HTTPS. `127.0.0.1` counts
as a "potentially trustworthy" origin, so classic mixed-content rules don't
block the call — but Chrome's Private Network Access check does: a public
page calling a loopback address sends a preflight with
`Access-Control-Request-Private-Network: true` and requires
`Access-Control-Allow-Private-Network: true` back, or the request is silently
blocked. `Program.cs` sets that header.

**CORS headers are echoed, not hardcoded.** The POS app attaches auth headers
to every outgoing request via an interceptor, which triggers a preflight
asking permission for those headers. Rather than maintaining a fixed
allow-list that breaks whenever the app adds a header, the agent echoes back
whatever the preflight asks for. It only serves origins already checked
against the allow-list, so this carries no real risk.

## Autostart mechanism

Autostart uses an `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry
pointing at `scripts\run-hidden.vbs` (which launches the exe with no console
window and logs to `logs\agent.log`).

Deliberately **not** a Scheduled Task: `Register-ScheduledTask` fails with
`Access is denied` (HRESULT 0x80070005) on a managed/corporate machine
without elevation, which would reintroduce the UAC prompt this per-user
install exists to avoid. A Windows Service would start before logon but also
needs admin, so it's ruled out for the same reason.

`scripts\install-autostart.ps1` / `uninstall-autostart.ps1` do the same thing
standalone, for setups that skip the installer.

Known limitation: autostart triggers at logon only. If the agent crashes
mid-shift, nothing restarts it until the next logon.

## Code signing

Neither the exe nor the installer is signed, so Windows shows an "unknown
publisher" warning on first run, and some managed environments treat
unsigned, low-prevalence executables with suspicion. Signing with an
Authenticode certificate (or Microsoft Trusted Signing, which suits an
existing Azure/Entra tenant) removes that friction — worth doing before any
wide rollout where non-technical staff install this themselves.
