"""Exercise the actual Orchestra CLI with Command -> Prompt -> Command -> Transform."""

import argparse
import json
import shutil
import sys
from pathlib import Path

from analyze import telemetry_facts
from cases import CASES
from grade import grade, read_json
from run import authenticated_environment, capture_process, collect_events, prepare_workspace, summarize_events, trial_environment, write_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Copilot runtime executable")
    parser.add_argument("--orchestra", type=Path, required=True, help="Built Orchestra.Cli.dll")
    parser.add_argument("--model", default="claude-opus-4.8")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-root", type=Path, required=True)
    options = parser.parse_args()
    evidence = options.output.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    workspace = options.work_root.resolve() / "workspace"
    home = options.work_root.resolve() / "copilot-home"
    case = CASES["instructions"]
    original = prepare_workspace(workspace, case["files"])
    env = trial_environment(authenticated_environment(), home, workspace, evidence)
    env["ORCHESTRA_COPILOT_CLI_PATH"] = str(options.cli.resolve())
    write_json(workspace / "orchestra.json", {"dataPath": str(options.work_root.resolve() / "data"), "defaultProvider": "copilot", "defaultModel": options.model})
    expected = {"policyCode": "TRIAGE-2026-10-Q7", "classifications": {"A": "critical", "B": "critical", "C": "high", "D": "normal", "E": "high"}}
    orchestration = {
        "name": "copilot-parity-mixed-cli", "description": "Real one-shot CLI integration test",
        "defaultModel": options.model, "defaultProvider": "copilot",
        "steps": [
            {"name": "prepare", "type": "Command", "command": sys.executable, "arguments": ["-c", "import pathlib; assert pathlib.Path('data/incidents.json').is_file(); print('Fixture inputs are ready.')"], "workingDirectory": str(workspace)},
            {"name": "task", "type": "Prompt", "dependsOn": ["prepare"], "systemPrompt": "", "systemPromptMode": "Append", "userPrompt": case["prompt"], "workingDirectory": str(workspace), "skillDirectories": [str(workspace / ".github/skills")], "reasoningLevel": "High", "contextTier": "Default", "timeoutSeconds": 180},
            {"name": "verify", "type": "Command", "dependsOn": ["task"], "command": sys.executable, "arguments": ["-c", f"import json,pathlib; result=json.loads(pathlib.Path('triage.json').read_text(encoding='utf-8-sig')); assert result == {expected!r}, result; pathlib.Path('dag-verified.txt').write_text('DAG_VERIFIED'); print('DAG_VERIFIED')"], "workingDirectory": str(workspace)},
            {"name": "report", "type": "Transform", "dependsOn": ["verify"], "template": "Deterministic verification: {{verify.output}}"},
        ],
    }
    write_json(evidence / "orchestration.json", orchestration)
    command = ["dotnet", str(options.orchestra.resolve()), "exec", "--run-file", str(evidence / "orchestration.json"), "--report", "json", "--report-output", str(evidence / "report.json"), "--detailed", "--no-interactive"]
    process = capture_process(command, workspace, env, evidence, 240)
    facts = summarize_events(collect_events(home, evidence, "orchestra"))
    acceptance = grade("instructions", workspace, evidence, original, facts["finalContent"])
    acceptance["checks"]["downstream_command_executed"] = (workspace / "dag-verified.txt").is_file()
    acceptance["checks"]["cli_succeeded"] = process["exitCode"] == 0
    acceptance["checks"]["report_written"] = (evidence / "report.json").is_file()
    acceptance["passed"] = all(acceptance["checks"].values())
    shutil.copytree(workspace, evidence / "workspace", ignore=shutil.ignore_patterns(".git", "__pycache__"))
    result = {"case": "mixed-cli", "arm": "orchestra-cli", "repeat": 1, "process": process, "facts": facts, "acceptance": acceptance, "workspace": str(workspace), "evidence": str(evidence), "telemetry": telemetry_facts(evidence, workspace)}
    write_json(evidence / "result.json", result)
    print(json.dumps({"process": process, "acceptance": acceptance}, indent=2))
    return 0 if acceptance["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
