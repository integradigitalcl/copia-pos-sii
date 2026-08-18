import subprocess
from pathlib import Path

from bus import all_tasks, complete_task, init_db, next_pending_task


STATE_DIR = Path(__file__).parent / "state"
RUNNER_REPORT = STATE_DIR / "runner_report.md"

ALLOWED_PREFIXES = (
    "dotnet ",
    "pwsh ",
    "python ",
    "git status",
    "git diff",
)


def instruction_to_command(instruction: str) -> str:
    text = instruction.lower()
    if "build" in text:
        return 'dotnet build "GrunflexPOS2.slnx"'
    if "test" in text:
        return 'dotnet test "GrunflexPOS.API.Tests/GrunflexPOS.API.Tests.csproj"'
    if "status" in text:
        return "git status --short"
    return "git diff --stat"


def is_allowed(cmd: str) -> bool:
    return cmd.startswith(ALLOWED_PREFIXES)


def run_command(cmd: str) -> tuple[int, str]:
    proc = subprocess.run(
        cmd,
        shell=True,
        cwd=str(Path(__file__).resolve().parents[1]),
        capture_output=True,
        text=True,
    )
    output = (proc.stdout or "") + ("\n" + proc.stderr if proc.stderr else "")
    return proc.returncode, output.strip()


def append_report(text: str) -> None:
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    with RUNNER_REPORT.open("a", encoding="utf-8") as f:
        f.write(text + "\n")


def main() -> None:
    init_db()
    append_report("# Runner Report\n")

    while True:
        task = next_pending_task()
        if task is None:
            break

        instruction = task["payload"].get("instruction", "").strip()
        cmd = instruction_to_command(instruction)
        append_report(f"- Task {task['id']}: {instruction}")
        append_report(f"  - Command: `{cmd}`")

        if not is_allowed(cmd):
            complete_task(task["id"], {"error": "Command blocked by policy", "cmd": cmd}, failed=True)
            append_report("  - Result: blocked by allowlist")
            continue

        code, out = run_command(cmd)
        complete_task(task["id"], {"exit_code": code, "output": out}, failed=(code != 0))
        append_report(f"  - Exit: {code}")
        append_report(f"  - Output: ```\n{out[:1500]}\n```")

    tasks = all_tasks()
    append_report("\n## Summary")
    append_report(f"- Total tasks: {len(tasks)}")
    append_report(f"- Done: {sum(1 for t in tasks if t['status'] == 'done')}")
    append_report(f"- Failed: {sum(1 for t in tasks if t['status'] == 'failed')}")
    print("Runner finished.")


if __name__ == "__main__":
    main()
