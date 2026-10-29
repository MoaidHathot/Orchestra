"""One actual interactive Copilot CLI turn in a Windows ConPTY, then /exit.

Optional supplemental experiment, not part of the portable three-arm runner.
Install pywinpty into a scratch directory and pass it with --python-site.
"""

import argparse
import datetime
import json
import select
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

from cases import CASES
from grade import grade, sha256
from run import authenticated_environment, collect_events, prepare_workspace, summarize_events, trial_environment, write_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--python-site", type=Path, required=True)
    parser.add_argument("--model", default="claude-opus-4.8")
    parser.add_argument("--case", choices=list(CASES), default="instructions")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-root", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=180)
    options = parser.parse_args()
    sys.path.insert(0, str(options.python_site.resolve()))
    from winpty import PtyProcess

    evidence = options.output.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    workspace = options.work_root.resolve() / "workspace"
    home = options.work_root.resolve() / "copilot-home"
    case = CASES[options.case]
    if case.get("mcp"):
        parser.error("The optional TUI probe currently uses filesystem-only cases")
    original = prepare_workspace(workspace, case["files"])
    env = trial_environment(authenticated_environment(), home, workspace, evidence)
    # A nested pseudoterminal is not the user's Windows Terminal. Do not let
    # inherited terminal identity trigger setup of their real terminal settings.
    for key in ("WT_SESSION", "WT_PROFILE_ID", "TERM_PROGRAM"):
        env.pop(key, None)
    session_id = str(uuid.uuid4())
    command = [str(options.cli.resolve()), "--no-auto-update", "--allow-all", "--no-ask-user", "--model", options.model, "--reasoning-effort", "high", "--context", "default", "--mode", "interactive", "--session-id", session_id, "--screen-reader", "--no-mouse", "--no-remote", "-i", case["prompt"]]
    write_json(evidence / "invocation.json", {"command": command, "cwd": str(workspace), "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "cliSha256": sha256(options.cli), "transport": "Windows ConPTY via pywinpty 3.1.0"})
    started = time.monotonic()
    process = PtyProcess.spawn(command, cwd=str(workspace), env=env, dimensions=(45, 180))
    transcript_path = home / "session-state" / session_id / "events.jsonl"
    exit_sent_at = None
    completed_turn = False
    timed_out = False
    terminal_tail = ""
    setup_dismissed = False
    try:
        with (evidence / "terminal.txt").open("w", encoding="utf-8", newline="\n") as terminal:
            while process.isalive():
                if time.monotonic() - started > options.timeout:
                    timed_out = True
                    break
                if select.select([process.fd], [], [], 0.2)[0]:
                    try:
                        text = process.read(65536)
                    except EOFError:
                        break
                    terminal.write(text.replace("\r\n", "\n").replace("\r", "\n"))
                    terminal.flush()
                    terminal_tail = (terminal_tail + text)[-12000:]
                    if not setup_dismissed and "Would you like to add this key binding" in terminal_tail:
                        process.write("\x1b")
                        setup_dismissed = True
                    if "\x1b[6n" in text:
                        process.write("\x1b[1;1R")
                if exit_sent_at is None and transcript_path.is_file():
                    events = []
                    for line in transcript_path.read_text(encoding="utf-8-sig").splitlines():
                        try:
                            events.append(json.loads(line))
                        except json.JSONDecodeError:
                            pass
                    root_messages = [event for event in events if event.get("type") == "assistant.message" and not event.get("agentId")]
                    if root_messages:
                        last = root_messages[-1]
                        data = last.get("data", {})
                        later = events[events.index(last) + 1:]
                        completed_turn = bool(data.get("content")) and not data.get("toolRequests") and any(event.get("type") == "assistant.turn_end" for event in later)
                        if completed_turn:
                            process.write("/exit\r")
                            exit_sent_at = time.monotonic()
                if exit_sent_at is not None and time.monotonic() - exit_sent_at > 15:
                    break
        exit_code = process.exitstatus
    finally:
        if process.isalive():
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=30)
        process.close(force=True)
    events = collect_events(home, evidence, "tui")
    facts = summarize_events(events)
    acceptance = grade(options.case, workspace, evidence, original, facts["finalContent"])
    shutil.copytree(workspace, evidence / "workspace", ignore=shutil.ignore_patterns(".git", "__pycache__"))
    (evidence / "final.txt").write_text(facts["finalContent"], encoding="utf-8", newline="\n")
    result = {"case": options.case, "arm": "tui", "repeat": 1, "process": {"exitCode": exit_code, "timedOut": timed_out, "elapsedSeconds": time.monotonic() - started}, "completedTurn": completed_turn, "exitCommandSent": exit_sent_at is not None, "terminalSetupDismissed": setup_dismissed, "facts": facts, "acceptance": acceptance, "workspace": str(workspace), "evidence": str(evidence)}
    write_json(evidence / "result.json", result)
    write_json(evidence / "summary.json", [result])
    print(json.dumps({"completedTurn": completed_turn, "acceptance": acceptance, "process": result["process"]}, indent=2))
    return 0 if completed_turn and acceptance["passed"] and not timed_out else 1


if __name__ == "__main__":
    raise SystemExit(main())
