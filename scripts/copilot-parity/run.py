"""Run matched live CLI / .NET SDK / Orchestra experiments and keep the evidence.

Requires an already authenticated gh CLI or a Copilot/GitHub token environment
variable. Tokens only enter child environments; they are never written to reports.
"""

import argparse
import collections
import datetime
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import time
from pathlib import Path

from cases import CASES, COMMON_FILES
from grade import grade, read_json, sha256

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
RUNNER = HERE / "bin/Debug/net10.0/ParityRunner.dll"


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def capture_process(command, cwd, env, evidence, timeout):
    """Record exit status separately from artifact correctness; bound the whole tree."""
    started = time.monotonic()
    write_json(evidence / "invocation.json", {"command": command, "cwd": str(cwd), "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "timeoutSeconds": timeout})
    with (evidence / "stdout.txt").open("w", encoding="utf-8", newline="\n") as stdout, (evidence / "stderr.txt").open("w", encoding="utf-8", newline="\n") as stderr:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=stdout, stderr=stderr, start_new_session=os.name != "nt")
        timed_out = False
        try:
            process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=30)
            else:
                import signal
                os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=30)
    result = {"exitCode": process.returncode, "timedOut": timed_out, "elapsedSeconds": round(time.monotonic() - started, 3)}
    write_json(evidence / "process.json", result)
    return result


def authenticated_environment():
    env = dict(os.environ)
    token = next((env[name] for name in ("COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN") if env.get(name)), None)
    if token is None:
        response = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True, timeout=30)
        if response.returncode != 0 or not response.stdout.strip():
            raise RuntimeError("No token found in the environment or gh auth. Authenticate with gh first.")
        token = response.stdout.strip()
    # Prevent ambient BYOK routing, home overrides, or extra instructions from
    # changing the treatment. Retain the same PATH/toolchain for all three arms.
    for key in list(env):
        if key.startswith("COPILOT_PROVIDER_") or key in ("COPILOT_CUSTOM_INSTRUCTIONS_DIRS", "ORCHESTRA_COPILOT_CLI_PATH", "COPILOT_CLI_PATH", "COPILOT_OFFLINE"):
            del env[key]
    env.update({"COPILOT_GITHUB_TOKEN": token, "COPILOT_AUTO_UPDATE": "false", "COPILOT_ALLOW_ALL": "true", "COPILOT_DISABLE_KEYTAR": "1", "NO_COLOR": "1", "PYTHONIOENCODING": "utf-8", "PYTHONDONTWRITEBYTECODE": "1"})
    return env


def trial_environment(base, home, workspace, evidence):
    home.mkdir(parents=True)
    write_json(home / "config.json", {"trusted_folders": [str(workspace)]})
    write_json(home / "mcp-config.json", {"mcpServers": {}})
    return {**base, "COPILOT_HOME": str(home), "COPILOT_OTEL_EXPORTER_TYPE": "file", "COPILOT_OTEL_FILE_EXPORTER_PATH": str(evidence / "telemetry.jsonl"), "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT": "true"}


def prepare_workspace(path, files):
    path.mkdir(parents=True)
    for name, content in {**COMMON_FILES, **files}.items():
        target = path / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8", newline="\n")
    subprocess.run(["git", "init", "--quiet", str(path)], check=True, capture_output=True)
    return {p.relative_to(path).as_posix(): sha256(p) for p in path.rglob("*") if p.is_file() and ".git" not in p.parts}


def collect_events(home, evidence, arm):
    raw = []
    for path in sorted((home / "session-state").glob("*/events.jsonl")):
        target = evidence / "sessions" / path.parent.name
        target.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target / "events.jsonl")
        for extra in ("workspace.yaml", "plan.md"):
            if (path.parent / extra).is_file():
                shutil.copy2(path.parent / extra, target / extra)
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            try:
                raw.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    # Persisted events omit ephemeral events. Direct SDK and CLI JSONL capture
    # these too; use those streams when available, never add duplicate streams.
    stream = evidence / ("driver-events.jsonl" if arm == "sdk" else "stdout.txt")
    if arm in ("sdk", "cli") and stream.is_file():
        live = []
        for line in stream.read_text(encoding="utf-8-sig").splitlines():
            try:
                item = json.loads(line)
                if "type" in item:
                    live.append(item)
            except json.JSONDecodeError:
                pass
        if any(event.get("type") == "assistant.message" for event in live):
            raw = live
    return raw


def summarize_events(events):
    counts = collections.Counter(event.get("type", "unknown") for event in events)
    tools = collections.Counter()
    failures = 0
    models = set()
    contents = []
    input_tokens = output_tokens = 0
    for event in events:
        data = event.get("data") or {}
        kind = event.get("type")
        if kind == "tool.execution_start":
            tools[data.get("toolName", "unknown")] += 1
        if kind == "tool.execution_complete" and data.get("success") is False:
            failures += 1
        if kind == "assistant.usage":
            if data.get("model"):
                models.add(data["model"])
            input_tokens += data.get("inputTokens") or 0
            output_tokens += data.get("outputTokens") or 0
        if kind == "assistant.message" and not event.get("agentId") and not data.get("parentToolCallId"):
            contents.append(data.get("content", ""))
        if kind == "session.start" and data.get("selectedModel"):
            models.add(data["selectedModel"])
    return {
        "eventCounts": dict(sorted(counts.items())),
        "tools": dict(sorted(tools.items())),
        "failedToolCalls": failures,
        "models": sorted(models),
        "inputTokensObserved": input_tokens,
        "outputTokensObserved": output_tokens,
        "finalContent": contents[-1] if contents else "",
    }


def run_trial(options, base_env, case_name, arm, repeat):
    case = CASES[case_name]
    key = f"{case_name}/{repeat}/{arm}"
    evidence = options.output / key
    evidence.mkdir(parents=True, exist_ok=False)
    root = options.work_root / key
    workspace, home = root / "workspace", root / "copilot-home"
    original = prepare_workspace(workspace, case["files"])
    write_json(evidence / "initial-files.json", original)
    (evidence / "prompt.txt").write_text(case["prompt"], encoding="utf-8", newline="\n")
    env = trial_environment(base_env, home, workspace, evidence)
    config = {
        "arm": arm, "cliPath": str(options.cli), "workingDirectory": str(workspace),
        "evidenceDirectory": str(evidence), "model": options.model, "reasoningEffort": "high",
        "prompt": case["prompt"], "timeoutSeconds": options.timeout,
    }
    mcp = None
    if case.get("mcp"):
        config.update({"mcpCommand": sys.executable, "mcpArgs": [str(HERE / "fixture_mcp.py"), str(evidence / "mcp-calls.jsonl")]})
        mcp = {"command": config["mcpCommand"], "args": config["mcpArgs"], "tools": ["*"]}
        write_json(evidence / "mcp.json", {"mcpServers": {"fixture": mcp}})
    if options.enable_discovery and arm == "sdk":
        config["enableConfigDiscovery"] = True
    if options.on_demand_instructions and arm == "sdk":
        config["enableOnDemandInstructions"] = True
    if options.explicit_skill and case_name == "instructions" and arm == "sdk":
        config["skillDirectory"] = str(workspace / ".github/skills")
    if arm == "cli":
        command = [str(options.cli), "--no-auto-update", "--allow-all", "--no-ask-user", "--model", options.model, "--reasoning-effort", "high", "--context", "default", "--mode", "interactive", "--output-format", "json", "--stream", "on", "-p", case["prompt"]]
        if mcp:
            command += ["--additional-mcp-config", "@" + str(evidence / "mcp.json")]
    else:
        if arm == "orchestra":
            orchestration = {
                "name": "copilot-parity-" + case_name, "description": "Controlled harness parity experiment",
                "defaultProvider": "copilot", "defaultModel": options.model,
                "steps": [{"name": "task", "type": "Prompt", "systemPrompt": "", "systemPromptMode": "Append", "userPrompt": case["prompt"], "reasoningLevel": "High", "contextTier": "Default", "workingDirectory": str(workspace), "timeoutSeconds": options.timeout}],
            }
            if mcp:
                orchestration["mcps"] = [{"name": "fixture", "type": "local", "command": config["mcpCommand"], "arguments": config["mcpArgs"]}]
                orchestration["steps"][0]["mcps"] = ["fixture"]
            if options.explicit_skill and case_name == "instructions":
                orchestration["steps"][0]["skillDirectories"] = [str(workspace / ".github/skills")]
            write_json(evidence / "orchestration.json", orchestration)
            config["orchestrationFile"] = str(evidence / "orchestration.json")
        write_json(evidence / "config.json", config)
        command = ["dotnet", str(options.runner), str(evidence / "config.json")]
    print(f"START {key}", flush=True)
    process = capture_process(command, workspace, env, evidence, options.timeout + 45)
    events = collect_events(home, evidence, arm)
    facts = summarize_events(events)
    if arm != "cli" and (evidence / "driver-result.json").is_file():
        driver = read_json(evidence / "driver-result.json")
        final_content = driver.get("content") or facts["finalContent"]
    else:
        driver = None
        final_content = facts["finalContent"]
    (evidence / "final.txt").write_text(final_content or "", encoding="utf-8", newline="\n")
    acceptance = grade(case_name, workspace, evidence, original, final_content)
    # Preserve the produced artifacts (not just answers) for independent review.
    shutil.copytree(workspace, evidence / "workspace", ignore=shutil.ignore_patterns(".git", "__pycache__"))
    result = {
        "case": case_name, "arm": arm, "repeat": repeat, "process": process,
        "acceptance": acceptance, "facts": facts,
        "promptSha256": hashlib.sha256(case["prompt"].encode()).hexdigest(),
        "workspace": str(workspace), "evidence": str(evidence),
        "driverStatus": driver.get("status") if driver else None,
        "enableConfigDiscovery": config.get("enableConfigDiscovery"),
        "enableOnDemandInstructions": config.get("enableOnDemandInstructions"),
        "explicitSkills": options.explicit_skill,
    }
    write_json(evidence / "result.json", result)
    print(f"END   {key}: exit={process['exitCode']} artifacts={'PASS' if acceptance['passed'] else 'FAIL'} tools={sum(facts['tools'].values())} time={process['elapsedSeconds']}s", flush=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Exact executable shared by all arms; auto-update is disabled")
    parser.add_argument("--runner", type=Path, default=RUNNER, help="Built ParityRunner.dll (default: standard Debug output)")
    parser.add_argument("--model", default="claude-opus-4.8")
    parser.add_argument("--cases", nargs="+", choices=list(CASES), default=[name for name in CASES if name != "nested-instructions"])
    parser.add_argument("--arms", nargs="+", choices=["cli", "sdk", "orchestra"], default=["cli", "sdk", "orchestra"])
    parser.add_argument("--repeats", type=int, default=1)
    parser.add_argument("--timeout", type=int, default=480)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-root", type=Path)
    parser.add_argument("--probe-only", action="store_true")
    parser.add_argument("--enable-discovery", action="store_true", help="Diagnostic treatment: SDK EnableConfigDiscovery=true")
    parser.add_argument("--on-demand-instructions", action="store_true", help="Diagnostic treatment: SDK EnableOnDemandInstructionDiscovery=true")
    parser.add_argument("--explicit-skill", action="store_true", help="Diagnostic treatment: explicitly register the fixture skill directory")
    options = parser.parse_args()
    options.cli, options.output, options.runner = options.cli.resolve(), options.output.resolve(), options.runner.resolve()
    if not options.cli.is_file() or not options.runner.is_file():
        parser.error("Build ParityRunner.csproj and supply an existing CLI executable first")
    if options.repeats < 1:
        parser.error("--repeats must be positive")
    options.output.mkdir(parents=True, exist_ok=False)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    temp = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "Temp/opencode"
    options.work_root = (options.work_root or temp / ("orchestra-copilot-parity-" + stamp)).resolve()
    env = authenticated_environment()
    metadata = {
        "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "orchestraCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip(),
        "cliPath": str(options.cli), "cliSha256": sha256(options.cli),
        "cliVersion": subprocess.check_output([str(options.cli), "--no-auto-update", "--version"], env=env, text=True).strip(),
        "platform": platform.platform(), "python": sys.version, "model": options.model, "reasoningEffort": "high", "sessionMode": "interactive",
        "repeats": options.repeats, "cases": options.cases, "arms": options.arms,
        "isolatedCopilotHomePerTrial": True, "permissionPolicy": "approveAll", "contextTier": "default",
        "workRoot": str(options.work_root), "enableConfigDiscovery": options.enable_discovery,
        "enableOnDemandInstructions": options.on_demand_instructions,
        "explicitSkills": options.explicit_skill,
        "driverSourceHashes": {p.name: sha256(p) for p in HERE.iterdir() if p.suffix in (".cs", ".py", ".csproj")},
    }
    write_json(options.output / "metadata.json", metadata)
    probe_dir = options.output / "probe"
    probe_dir.mkdir()
    probe_workspace = options.work_root / "probe/workspace"
    prepare_workspace(probe_workspace, {})
    probe_home = options.work_root / "probe/copilot-home"
    probe_env = trial_environment(env, probe_home, probe_workspace, probe_dir)
    write_json(probe_dir / "config.json", {"arm": "probe", "cliPath": str(options.cli), "workingDirectory": str(probe_workspace), "evidenceDirectory": str(probe_dir), "timeoutSeconds": 90})
    status = capture_process(["dotnet", str(options.runner), str(probe_dir / "config.json")], probe_workspace, probe_env, probe_dir, 120)
    if status["exitCode"] != 0:
        raise RuntimeError(f"Preflight failed; see {probe_dir}")
    probe = read_json(probe_dir / "driver-result.json")
    if not probe.get("isAuthenticated") or options.model not in {model["id"] for model in probe["models"]}:
        raise RuntimeError(f"Requested model not available or authentication missing; see {probe_dir}")
    print(f"PREFLIGHT OK runtime={probe['runtime']['version']} model={options.model}", flush=True)
    if options.probe_only:
        return 0
    results = []
    for repeat in range(1, options.repeats + 1):
        for index, case_name in enumerate(options.cases):
            # Rotate treatment order so the CLI is not invariably the cold/first arm.
            shift = (repeat - 1 + index) % len(options.arms)
            for arm in options.arms[shift:] + options.arms[:shift]:
                results.append(run_trial(options, env, case_name, arm, repeat))
                write_json(options.output / "summary.json", results)
    return 0 if all(r["acceptance"]["passed"] and r["process"]["exitCode"] == 0 for r in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
