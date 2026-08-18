import os
from pathlib import Path
from openai import OpenAI

from bus import enqueue_task, init_db


STATE_DIR = Path(__file__).parent / "state"
GOAL_FILE = STATE_DIR / "goal.txt"
PLAN_FILE = STATE_DIR / "plan.md"


def build_plan(goal: str) -> str:
    model = os.getenv("AGENT_MODEL", "gpt-4o-mini")
    client = OpenAI()
    prompt = f"""
You are a planning agent for software delivery.
Create a concise implementation plan for this goal:
{goal}

Return:
1) 5-10 actionable tasks
2) each task in one line, imperative, no numbering decoration besides "- "
"""
    resp = client.responses.create(model=model, input=prompt)
    return resp.output_text.strip()


def parse_tasks(plan_text: str) -> list[str]:
    tasks = []
    for line in plan_text.splitlines():
        line = line.strip()
        if line.startswith("- "):
            tasks.append(line[2:].strip())
    return [t for t in tasks if t]


def main() -> None:
    init_db()
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    if not GOAL_FILE.exists():
        GOAL_FILE.write_text("Define hardening and execute safe implementation tasks.", encoding="utf-8")

    goal = GOAL_FILE.read_text(encoding="utf-8").strip()
    plan = build_plan(goal)
    PLAN_FILE.write_text(plan, encoding="utf-8")

    for item in parse_tasks(plan):
        enqueue_task("planned-task", {"instruction": item})

    print("Planner finished. Tasks enqueued.")


if __name__ == "__main__":
    main()
