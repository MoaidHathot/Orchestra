"""Extract effective model requests from OTel, keeping raw experiment results intact."""

import argparse
import difflib
import hashlib
import json
import re
import subprocess
from pathlib import Path

from grade import read_json, sha256
from run import write_json


def normalize(text, workspace):
    # Normalize only known run-specific locations/IDs/dates, not instructions.
    workspace = str(workspace)
    home = str(Path(workspace).parent / "copilot-home")
    for source, target in ((workspace, "<WORKSPACE>"), (home, "<COPILOT_HOME>")):
        text = text.replace(source, target).replace(source.replace("\\", "/"), target)
        text = text.replace(source.replace("\\", "\\\\"), target)
    text = re.sub(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "<UUID>", text)
    text = re.sub(r"<current_datetime>.*?</current_datetime>", "<current_datetime><TIME></current_datetime>", text)
    return text.replace("\r\n", "\n")


def telemetry_facts(evidence, workspace):
    path = evidence / "telemetry.jsonl"
    if not path.is_file():
        return {"error": "No telemetry file; effective request not verified"}
    records = [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    calls = [r["attributes"] for r in records if r.get("type") == "span" and r.get("attributes", {}).get("gen_ai.operation.name") == "chat"]
    if not calls:
        return {"error": "No chat spans; effective request not verified"}
    first = calls[0]
    blocks = json.loads(first.get("gen_ai.system_instructions", "[]"))
    system = "\n\n".join(block.get("content", "") for block in blocks)
    normalized = normalize(system, workspace)
    (evidence / "effective-system-prompt.txt").write_text(system, encoding="utf-8", newline="\n")
    (evidence / "normalized-system-prompt.txt").write_text(normalized, encoding="utf-8", newline="\n")
    tools = json.loads(first.get("gen_ai.tool.definitions", "[]"))
    write_json(evidence / "effective-tools.json", tools)
    tool_names = [tool.get("name") or tool.get("function", {}).get("name") for tool in tools]
    summary = {
        "chatCalls": len(calls),
        "requestedModels": sorted({c.get("gen_ai.request.model", "unknown") for c in calls}),
        "responseModels": sorted({c.get("gen_ai.response.model", "unknown") for c in calls}),
        "reasoningEfforts": sorted({c.get("gen_ai.request.reasoning.level", "unspecified") for c in calls}),
        "runtimeVersions": sorted({r.get("resource", {}).get("attributes", {}).get("service.version", "unknown") for r in records}),
        "inputTokens": sum(c.get("gen_ai.usage.input_tokens", 0) for c in calls),
        "outputTokens": sum(c.get("gen_ai.usage.output_tokens", 0) for c in calls),
        "reasoningTokensReported": sum(c.get("gen_ai.usage.reasoning.output_tokens", 0) for c in calls),
        "initialToolNames": sorted(name for name in tool_names if name),
        "initialToolCount": len(tools),
        "systemPromptCharacters": len(system),
        "systemPromptSha256": hashlib.sha256(system.encode()).hexdigest(),
        "normalizedSystemPromptSha256": hashlib.sha256(normalized.encode()).hexdigest(),
        "telemetrySha256": sha256(path),
    }
    write_json(evidence / "telemetry-summary.json", summary)
    return summary


def git_audit(workspace):
    process = subprocess.run(["git", "-C", str(workspace), "log", "--all", "--format=%H"], capture_output=True, text=True, timeout=15)
    if process.returncode != 0:
        # An unborn repository has no commits; rev-list --all handles that case.
        count = subprocess.run(["git", "-C", str(workspace), "rev-list", "--count", "--all"], capture_output=True, text=True, timeout=15)
        if count.returncode == 0 and count.stdout.strip() == "0":
            return {"initialCommitCount": 0, "finalCommitCount": 0, "commitIds": [], "noCommitInstructionSatisfied": True}
        return {"auditError": "Git history unavailable", "exitCode": process.returncode}
    commits = process.stdout.splitlines()
    return {"initialCommitCount": 0, "finalCommitCount": len(commits), "commitIds": commits, "noCommitInstructionSatisfied": not commits}


def analyze(root):
    results = read_json(root / "summary.json")
    rows = []
    by_case = {}
    for result in results:
        evidence = Path(result["evidence"])
        facts = telemetry_facts(evidence, result["workspace"])
        row = {"case": result["case"], "arm": result["arm"], "repeat": result["repeat"], "process": result["process"], "acceptance": result["acceptance"], "observedTools": result["facts"]["tools"], "telemetry": facts, "resultSha256": sha256(evidence / "result.json")}
        row["postRunGitAudit"] = git_audit(result["workspace"])
        write_json(evidence / "git-audit.json", row["postRunGitAudit"])
        driver_path = evidence / "driver-result.json"
        if driver_path.is_file():
            driver = read_json(driver_path)
            delivered = driver.get("content") or ""
            transcript = result["facts"]["finalContent"]
            row["outputProjection"] = {
                "status": driver.get("status"),
                "deliveredCharacters": len(delivered),
                "transcriptFinalCharacters": len(transcript),
                "deliveredEqualsTranscriptFinal": delivered == transcript,
                "deliveredInstructionMarkers": {"agents_md": "PROJECT_CANARY_6C21" in delivered, "copilot_instructions_md": "SOURCE_POLICY_74B9" in delivered},
            }
        rows.append(row)
        by_case.setdefault((row["case"], row["repeat"]), {})[row["arm"]] = evidence
    comparisons = []
    for (case, repeat), arms in by_case.items():
        for left, right in (("cli", "sdk"), ("sdk", "orchestra"), ("cli", "orchestra")):
            if left not in arms or right not in arms:
                continue
            left_path, right_path = arms[left] / "normalized-system-prompt.txt", arms[right] / "normalized-system-prompt.txt"
            if not left_path.is_file() or not right_path.is_file():
                continue
            difference = "".join(difflib.unified_diff(left_path.read_text(encoding="utf-8").splitlines(keepends=True), right_path.read_text(encoding="utf-8").splitlines(keepends=True), fromfile=left, tofile=right))
            (root / f"{case}-{repeat}-{left}-vs-{right}.diff").write_text(difference, encoding="utf-8", newline="\n")
            catalogs = {}
            for arm in (left, right):
                result = read_json(arms[arm] / "result.json")
                catalogs[arm] = {(tool.get("name") or tool.get("function", {}).get("name")): normalize(json.dumps(tool, sort_keys=True), result["workspace"]) for tool in read_json(arms[arm] / "effective-tools.json")}
            common = catalogs[left].keys() & catalogs[right].keys()
            comparisons.append({"case": case, "repeat": repeat, "left": left, "right": right, "normalizedSystemPromptsEqual": not difference, "commonToolCount": len(common), "leftOnlyTools": sorted(catalogs[left].keys() - catalogs[right].keys()), "rightOnlyTools": sorted(catalogs[right].keys() - catalogs[left].keys()), "changedCommonToolDefinitions": sorted(name for name in common if catalogs[left][name] != catalogs[right][name])})
    write_json(root / "comparisons.json", comparisons)
    write_json(root / "analysis.json", rows)
    for row in rows:
        facts = row["telemetry"]
        print(f"{row['case']:14} {row['repeat']} {row['arm']:10} artifacts={'PASS' if row['acceptance']['passed'] else 'FAIL':4} calls={facts.get('chatCalls', '?')} tools={facts.get('initialToolCount', '?')} model={','.join(facts.get('responseModels', []))} reasoning={','.join(facts.get('reasoningEfforts', []))}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", type=Path, nargs="+")
    options = parser.parse_args()
    for directory in options.directories:
        analyze(directory.resolve())
