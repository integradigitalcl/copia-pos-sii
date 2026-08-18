# Local Multi-Agent Setup (Planner + Runner)

This folder contains a 2-agent local orchestration setup:

- `planner_agent.py`: breaks goals into tasks and queues them.
- `runner_agent.py`: executes queued tasks (safe command allowlist).
- `bus.py`: SQLite message/task queue.
- `orchestrator.py`: starts planner and runner in parallel.

## Quick start (Windows PowerShell)

1. Create a virtual environment:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
```

2. Install dependencies:

```powershell
pip install -r agents\requirements.txt
```

3. Configure model/API key (choose one):

```powershell
$env:OPENAI_API_KEY="YOUR_KEY"
# Optional model override:
$env:AGENT_MODEL="gpt-4o-mini"
```

4. Run both agents:

```powershell
pwsh .\agents\run_agents.ps1
```

## How they collaborate

- Planner writes tasks into SQLite queue (`agents/state/agent_bus.db`).
- Runner picks `pending` tasks, executes safe commands, and writes logs/artifacts.
- Both share context through DB and markdown outputs in `agents/state/`.

## Guardrails

- Runner only executes commands that start with:
  - `dotnet`
  - `pwsh`
  - `python`
  - `git status`
  - `git diff`
- Any other command is rejected and marked failed.

## Typical workflow with this chat

1. You give goal to planner via `agents/state/goal.txt`.
2. Agents process and produce:
   - `agents/state/plan.md`
   - `agents/state/runner_report.md`
3. You share outputs here and I review/refine/fix.

## WhatsApp bridge (official Cloud API)

Files:
- `agents/whatsapp_bridge.py`
- `agents/run_whatsapp_bridge.ps1`

Required env vars:
- `WA_VERIFY_TOKEN` (custom token for Meta webhook verify)
- `WA_ACCESS_TOKEN` (Meta Cloud API token)
- `WA_PHONE_NUMBER_ID` (Meta phone number id)

Run:

```powershell
$env:WA_VERIFY_TOKEN="CHANGE_ME_VERIFY_TOKEN"
$env:WA_ACCESS_TOKEN="EAA..."
$env:WA_PHONE_NUMBER_ID="1234567890"
pwsh .\agents\run_whatsapp_bridge.ps1
```

Expose your local webhook (example with ngrok):

```powershell
ngrok http 8080
```

Then configure Meta webhook URL as:
- `https://<ngrok-domain>/whatsapp/webhook`

Supported inbound commands:
- `status` or `/status` -> returns queue summary
- `goal <text>` or `/goal <text>` -> updates `agents/state/goal.txt`
- Any other text -> enqueues a task in SQLite bus
