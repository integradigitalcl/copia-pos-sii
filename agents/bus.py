import json
import sqlite3
from pathlib import Path
from typing import Any


DB_PATH = Path(__file__).parent / "state" / "agent_bus.db"


def _conn() -> sqlite3.Connection:
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn


def init_db() -> None:
    with _conn() as c:
        c.execute(
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                payload TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                result TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
            )
            """
        )
        c.commit()


def enqueue_task(title: str, payload: dict[str, Any]) -> int:
    with _conn() as c:
        cur = c.execute(
            "INSERT INTO tasks (title, payload, status) VALUES (?, ?, 'pending')",
            (title, json.dumps(payload, ensure_ascii=True)),
        )
        c.commit()
        return int(cur.lastrowid)


def next_pending_task() -> dict[str, Any] | None:
    with _conn() as c:
        row = c.execute(
            "SELECT * FROM tasks WHERE status='pending' ORDER BY id LIMIT 1"
        ).fetchone()
        if row is None:
            return None
        c.execute(
            "UPDATE tasks SET status='in_progress', updated_at=CURRENT_TIMESTAMP WHERE id=?",
            (row["id"],),
        )
        c.commit()
        return {
            "id": row["id"],
            "title": row["title"],
            "payload": json.loads(row["payload"]),
            "status": "in_progress",
        }


def complete_task(task_id: int, result: dict[str, Any], failed: bool = False) -> None:
    status = "failed" if failed else "done"
    with _conn() as c:
        c.execute(
            "UPDATE tasks SET status=?, result=?, updated_at=CURRENT_TIMESTAMP WHERE id=?",
            (status, json.dumps(result, ensure_ascii=True), task_id),
        )
        c.commit()


def all_tasks() -> list[dict[str, Any]]:
    with _conn() as c:
        rows = c.execute("SELECT * FROM tasks ORDER BY id").fetchall()
        return [
            {
                "id": r["id"],
                "title": r["title"],
                "payload": json.loads(r["payload"]),
                "status": r["status"],
                "result": json.loads(r["result"]) if r["result"] else None,
            }
            for r in rows
        ]
