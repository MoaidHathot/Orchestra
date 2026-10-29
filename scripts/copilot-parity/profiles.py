"""Probe documented SDK configuration and mode RPCs without changing Orchestra."""

import argparse
import os
import subprocess
from pathlib import Path

from analyze import telemetry_facts
from cases import CASES
from grade import read_json
from run import authenticated_environment, capture_process, collect_events, prepare_workspace, trial_environment, write_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--runner", type=Path, required=True)
    parser.add_argument("--model", default="claude-opus-4.8")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-root", type=Path, required=True)
    parser.add_argument("--catalog-only", action="store_true", help="No inference; inspect discovered skills and mode RPCs only")
    parser.add_argument("--surface-only", action="store_true", help="Capture SDK prompts/tools with native frontend-context options")
    options = parser.parse_args()
    options.output = options.output.resolve()
    options.output.mkdir(parents=True, exist_ok=False)
    env = authenticated_environment()
    configurations = {
        "defaults": {"arm": "inspect"},
        "discovery": {"arm": "inspect", "enableConfigDiscovery": True},
        "plan": {"arm": "inspect", "sessionMode": "plan"},
        "autopilot": {"arm": "inspect", "sessionMode": "autopilot"},
    }
    if not options.catalog_only:
        configurations["cli-features"] = {"arm": "sdk", "enableConfigDiscovery": True, "enableSessionStore": True, "memoryEnabled": True}
    if options.surface_only:
        configurations = {
            "sdk-noninteractive": {"arm": "sdk", "runningInInteractiveMode": False},
            "cli-interactive-context": {"arm": "sdk", "enableConfigDiscovery": True, "enableOnDemandInstructions": True, "enableSessionStore": True, "memoryEnabled": True, "agentContext": "cli", "runningInInteractiveMode": True},
            "cli-prompt-context": {"arm": "sdk", "enableConfigDiscovery": True, "enableOnDemandInstructions": True, "enableSessionStore": True, "memoryEnabled": False, "agentContext": "cli", "runningInInteractiveMode": False},
        }
    results = []
    for name, additions in configurations.items():
        evidence = options.output / name
        evidence.mkdir()
        workspace = options.work_root.resolve() / name / "workspace"
        home = options.work_root.resolve() / name / "copilot-home"
        prepare_workspace(workspace, CASES["instructions"]["files"])
        trial_env = trial_environment(env, home, workspace, evidence)
        config = {"cliPath": str(options.cli.resolve()), "workingDirectory": str(workspace), "evidenceDirectory": str(evidence), "model": options.model, "reasoningEffort": "high", "timeoutSeconds": 90, "prompt": "Reply with PROFILE_OK. Do not call tools, change files, or store memories.", **additions}
        write_json(evidence / "config.json", config)
        process = capture_process(["dotnet", str(options.runner.resolve()), str(evidence / "config.json")], workspace, trial_env, evidence, 120)
        collect_events(home, evidence, "sdk")
        result = {"profile": name, "process": process, "evidence": str(evidence)}
        for field in ("runtime", "initial-mode", "mode", "skills", "plan", "driver-result"):
            path = evidence / (field + ".json")
            if path.is_file():
                result[field] = read_json(path)
        if additions["arm"] == "sdk":
            result["telemetry"] = telemetry_facts(evidence, workspace)
        results.append(result)
        print(f"{name}: exit={process['exitCode']} mode={result.get('mode')} skills={[s['name'] for s in result.get('skills', {}).get('skills', [])]}", flush=True)
        write_json(options.output / "profiles.json", results)
    return 0 if all(row["process"]["exitCode"] == 0 for row in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
