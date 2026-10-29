"""Synthetic SDK-event contract probe: a child's idle must not complete the parent.

No CLI processes or agents are started and no inference occurs. Exit 1 means the
completion adapter violated the contract; exit 0 means the tested ordering passed.
"""

import argparse
import json
from pathlib import Path

from grade import read_json
from run import REPO, RUNNER, capture_process, write_json


def main():
    import os

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runner", type=Path, default=RUNNER)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    evidence = options.output.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    write_json(evidence / "config.json", {"arm": "lifecycle-probe", "cliPath": "unused-by-this-probe", "workingDirectory": str(REPO), "evidenceDirectory": str(evidence), "timeoutSeconds": 30})
    process = capture_process(["dotnet", str(options.runner.resolve()), str(evidence / "config.json")], REPO, dict(os.environ), evidence, 45)
    result = read_json(evidence / "driver-result.json")
    print(json.dumps({key: value for key, value in result.items() if key != "translatedEvents"}, indent=2))
    return process["exitCode"]


if __name__ == "__main__":
    raise SystemExit(main())
