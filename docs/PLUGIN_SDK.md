# Ripple Plugin SDK & Agent Hook Guide

## Manifest Plugins (`plugins/<plugin-id>/plugin.json`)

Place any plugin folder under `plugins/` alongside `Ripple.exe`. Ripple discovers manifests automatically on startup via `PluginHostService`.

### Schema
```json
{
  "id": "my-plugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "Short summary shown in Notchy & Settings",
  "icon": "✦",
  "accentHex": "#6C8CFF",
  "entryType": "http-json | local-action",
  "endpointUrl": "https://example.com/status.json",
  "scriptCommand": "ripple push --title \"Done\"",
  "refreshIntervalSeconds": 45,
  "permissions": ["network"]
}
```

### Included Sample Plugins
- `plugins/crypto-ticker/plugin.json` — Live BTC price ticker
- `plugins/github-notifications/plugin.json` — GitHub notifications & CI status
- `plugins/spotify-like-button/plugin.json` — One-tap Favorite quick action

---

## Local REST & WebSocket API

Base URL: `http://127.0.0.1:49152` (configurable in `%LocalAppData%\Ripple\settings.json`)
Header: `X-Ripple-Token: <token>` (or `Authorization: Bearer <token>`)

### Endpoints
- `GET /health` — Unauthenticated liveness probe
- `GET /api/v1/status` — Current state, active module, profile & module health
- `POST /api/v1/push` — Publish a Live Activity capsule (`title`, `subtitle`, `icon`, `accentHex`, `durationSeconds`, `progress`, `priority`, `coalesceKey`)
- `POST /api/v1/timer` — Start a Focus timer (`minutes`, `label`)
- `POST /api/v1/agent/event` — Report AI agent activity (`agentName`, `status`, `title`, `detail`, `actionId`, `tokensUsed`)
- `POST /api/v1/profile` — Switch profile (`name`)
- `GET /ws` — WebSocket live push stream

---

## AI Coding Agent Hooks (Claude Code / Codex CLI / Gemini CLI)

Open **Settings → Integrations → Install Claude/Codex/Gemini Hook Script** (or Notchy tab → **Install Hook**). This generates `%LocalAppData%\Ripple\hooks\ripple-agent-hook.ps1`, which can be invoked from any CLI hook or wrapper:

```powershell
powershell -File "$env:LOCALAPPDATA\Ripple\hooks\ripple-agent-hook.ps1" -Agent "Claude Code" -Status "permission" -Title "Run migration?" -Detail "Allow database schema update"
```

Clicking **Approve** or **Deny** in the Ripple notch writes a signed local JSON decision file to `%LocalAppData%\Ripple\agent_decisions\<actionId>.json`.
