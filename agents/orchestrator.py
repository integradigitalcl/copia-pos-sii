import subprocess
import sys
from pathlib import Path


BASE = Path(__file__).parent


def run(script: str) -> int:
    return subprocess.call([sys.executable, str(BASE / script)], cwd=str(BASE))


def main() -> None:
    planner_code = run("planner_agent.py")
    if planner_code != 0:
        raise SystemExit(planner_code)

    runner_code = run("runner_agent.py")
    if runner_code != 0:
        raise SystemExit(runner_code)

    print("All agents completed.")


if __name__ == "__main__":
    main()
