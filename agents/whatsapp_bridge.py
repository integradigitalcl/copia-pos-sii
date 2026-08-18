import os
from pathlib import Path
from typing import Any

import httpx
from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.responses import JSONResponse, PlainTextResponse

from agents.bus import all_tasks, enqueue_task, init_db


app = FastAPI(title="Grunflex Agents WhatsApp Bridge")

VERIFY_TOKEN = os.getenv("WA_VERIFY_TOKEN", "")
WA_ACCESS_TOKEN = os.getenv("WA_ACCESS_TOKEN", "")
WA_PHONE_NUMBER_ID = os.getenv("WA_PHONE_NUMBER_ID", "")

STATE_DIR = Path(__file__).parent / "state"
GOAL_FILE = STATE_DIR / "goal.txt"


def latest_summary() -> str:
    tasks = all_tasks()
    if not tasks:
        return "No hay tareas en cola."
    done = sum(1 for t in tasks if t["status"] == "done")
    failed = sum(1 for t in tasks if t["status"] == "failed")
    pending = sum(1 for t in tasks if t["status"] in ("pending", "in_progress"))
    return f"Estado agentes -> total:{len(tasks)} done:{done} failed:{failed} pending:{pending}"


async def send_whatsapp_text(to_number: str, text: str) -> None:
    if not WA_ACCESS_TOKEN or not WA_PHONE_NUMBER_ID:
        return
    url = f"https://graph.facebook.com/v20.0/{WA_PHONE_NUMBER_ID}/messages"
    payload = {
        "messaging_product": "whatsapp",
        "to": to_number,
        "type": "text",
        "text": {"body": text[:4000]},
    }
    headers = {"Authorization": f"Bearer {WA_ACCESS_TOKEN}"}
    async with httpx.AsyncClient(timeout=20) as client:
        resp = await client.post(url, headers=headers, json=payload)
        if resp.status_code >= 300:
            raise HTTPException(status_code=500, detail=f"WhatsApp send error: {resp.text}")


def extract_incoming_messages(payload: dict[str, Any]) -> list[tuple[str, str]]:
    out: list[tuple[str, str]] = []
    for entry in payload.get("entry", []):
        for change in entry.get("changes", []):
            value = change.get("value", {})
            messages = value.get("messages", [])
            for msg in messages:
                from_number = msg.get("from")
                if msg.get("type") == "text":
                    text = (msg.get("text") or {}).get("body", "").strip()
                    if from_number and text:
                        out.append((from_number, text))
    return out


@app.get("/whatsapp/webhook")
def verify_webhook(
    hub_mode: str = Query("", alias="hub.mode"),
    hub_verify_token: str = Query("", alias="hub.verify_token"),
    hub_challenge: str = Query("", alias="hub.challenge"),
):
    if hub_mode == "subscribe" and hub_verify_token == VERIFY_TOKEN:
        return PlainTextResponse(content=hub_challenge, status_code=200)
    return PlainTextResponse(content="Forbidden", status_code=403)


@app.post("/whatsapp/webhook")
async def receive_webhook(request: Request):
    payload = await request.json()
    messages = extract_incoming_messages(payload)
    if not messages:
        return JSONResponse({"ok": True, "processed": 0})

    init_db()
    STATE_DIR.mkdir(parents=True, exist_ok=True)

    for from_number, text in messages:
        lower = text.lower()
        if lower in ("/status", "status", "estado"):
            await send_whatsapp_text(from_number, latest_summary())
            continue

        if lower.startswith("/goal ") or lower.startswith("goal "):
            goal = text.split(" ", 1)[1].strip()
            GOAL_FILE.write_text(goal, encoding="utf-8")
            await send_whatsapp_text(from_number, "Objetivo actualizado en agents/state/goal.txt")
            continue

        # default: enqueue as instruction for planner/runner ecosystem
        task_id = enqueue_task("whatsapp-instruction", {"instruction": text, "source": "whatsapp", "from": from_number})
        await send_whatsapp_text(from_number, f"Tarea encolada con id {task_id}. Usa /status para ver avance.")

    return JSONResponse({"ok": True, "processed": len(messages)})


@app.get("/health")
def health():
    return {"ok": True}
