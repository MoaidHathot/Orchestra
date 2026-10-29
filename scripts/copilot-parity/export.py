"""Export a compact evidence index; raw prompts, workspaces and telemetry stay local."""

import argparse
from pathlib import Path

from grade import read_json, sha256
from run import write_json


def export(root, output):
    primary = ["instructions-baseline", "baseline-complete"]
    supplemental = ["discovery-enabled", "explicit-skills", "nested-baseline", "nested-enabled", "nested-discovery-only", "interactive-tui-confirmation"]
    baseline = read_json(root / "baseline-complete/metadata.json")
    document = {
        "date": "2026-10-29",
        "orchestraCommit": baseline["orchestraCommit"],
        "sdkVersion": "1.0.14",
        "reviewedSdkSourceCommit": "e60d9037353249ef16b349eb4012e8c1d113fda5",
        "runtimeVersion": "1.0.85",
        "runtimeSha256": baseline["cliSha256"],
        "installedCliVersionProbed": "1.0.91",
        "model": baseline["model"],
        "reasoningEffort": baseline["reasoningEffort"],
        "primaryExperiments": primary,
        "supplementalExperiments": supplemental,
        "rawEvidenceRoot": "artifacts/copilot-parity",
        "primaryResults": [],
        "supplementalResults": [],
        "requestComparisons": [],
        "sdkProfiles": [],
    }
    for experiment in primary + supplemental:
        folder = root / experiment
        analysis = read_json(folder / "analysis.json")
        if experiment in primary:
            cases = ["instructions"] if experiment == "instructions-baseline" else ["repair", "graph", "analysis", "automation", "mcp-recovery"]
            repeats = [1] if experiment == "instructions-baseline" else [1, 2]
            expected = {(case, arm, repeat) for case in cases for arm in ("cli", "sdk", "orchestra") for repeat in repeats}
            observed = {(row["case"], row["arm"], row["repeat"]) for row in analysis}
            if observed != expected or len(analysis) != len(expected):
                raise ValueError(f"Incomplete or duplicate primary cohort: {experiment}; missing={sorted(expected - observed)}")
        for result in analysis:
            trial = folder if result["arm"] == "tui" else folder / result["case"] / str(result["repeat"]) / result["arm"]
            telemetry = result["telemetry"]
            row = {
                "experiment": experiment,
                "case": result["case"], "arm": result["arm"], "repeat": result["repeat"],
                "exitCode": result["process"]["exitCode"],
                "timedOut": result["process"]["timedOut"],
                "artifactAcceptance": result["acceptance"]["passed"],
                "failedChecks": [name for name, passed in result["acceptance"]["checks"].items() if not passed],
                "instructionMarkers": result["acceptance"]["instructions"],
                "modelCalls": telemetry.get("chatCalls"),
                "responseModels": telemetry.get("responseModels"),
                "reasoningEfforts": telemetry.get("reasoningEfforts"),
                "observedTools": result["observedTools"],
                "postRunGitAudit": result["postRunGitAudit"],
                "resultSha256": result["resultSha256"],
                "telemetrySha256": telemetry.get("telemetrySha256"),
                "evidence": trial.relative_to(root).as_posix(),
            }
            if "outputProjection" in result:
                row["outputProjection"] = result["outputProjection"]
            document["primaryResults" if experiment in primary else "supplementalResults"].append(row)
        if experiment == "instructions-baseline":
            document["requestComparisons"] = read_json(folder / "comparisons.json")
    for experiment in ("sdk-profiles", "sdk-profiles-1.0.91", "sdk-surface-profiles"):
        for profile in read_json(root / experiment / "profiles.json"):
            settings = read_json(root / experiment / profile["profile"] / "config.json")
            row = {
                "experiment": experiment, "profile": profile["profile"],
                "exitCode": profile["process"]["exitCode"],
                "runtime": profile.get("runtime", {}).get("runtime"),
                "initialMode": profile.get("initial-mode"), "selectedMode": profile.get("mode"),
                "skillNames": [skill["name"] for skill in profile.get("skills", {}).get("skills", [])],
                "explicitSettings": {name: settings[name] for name in ("enableConfigDiscovery", "enableOnDemandInstructions", "enableSessionStore", "memoryEnabled", "agentContext", "runningInInteractiveMode") if name in settings},
            }
            if "plan" in profile:
                row["planExists"] = profile["plan"]["exists"]
                row["planContent"] = profile["plan"]["content"]
            if "telemetry" in profile:
                row["offeredTools"] = profile["telemetry"]["initialToolNames"]
                row["telemetrySha256"] = profile["telemetry"]["telemetrySha256"]
                prompt = (root / experiment / profile["profile"] / "normalized-system-prompt.txt").read_text(encoding="utf-8")
                row["nonInteractivePreamble"] = "non-interactive mode" in prompt.splitlines()[0]
            document["sdkProfiles"].append(row)
    mixed = read_json(root / "mixed-cli/result.json")
    report = read_json(root / "mixed-cli/report.json")
    document["mixedCli"] = {
        "exitCode": mixed["process"]["exitCode"],
        "acceptance": mixed["acceptance"]["checks"],
        "runStatus": report["status"], "isIncomplete": report["isIncomplete"],
        "steps": {step["name"]: step["status"] for step in report["steps"]},
        "finalContent": report["finalContent"],
        "reportSha256": sha256(root / "mixed-cli/report.json"),
        "evidence": "mixed-cli",
    }
    lifecycle = read_json(root / "lifecycle-confirmation/driver-result.json")
    document["lifecycleProbe"] = {
        **{key: value for key, value in lifecycle.items() if key != "translatedEvents"},
        "processExitCode": read_json(root / "lifecycle-confirmation/process.json")["exitCode"],
        "evidence": "lifecycle-confirmation",
        "resultSha256": sha256(root / "lifecycle-confirmation/driver-result.json"),
    }
    prose_path = root / "baseline-complete/analysis/1/cli/workspace/report.md"
    document["manualReviewNotes"] = [{
        "evidence": prose_path.relative_to(root).as_posix(),
        "sha256": sha256(prose_path),
        "observation": "The required decision and cost are correct, but the prose describes the budget reduction from 12000 to 11500 as roughly 3%; it is 4.1667%.",
        "scope": "Incidental prose arithmetic not covered by the automated acceptance checks; no general quality ranking inferred.",
    }]
    write_json(output, document)
    print(f"Exported {len(document['primaryResults'])} primary and {len(document['supplementalResults'])} supplemental task runs to {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path("artifacts/copilot-parity"))
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    export(options.root.resolve(), options.output.resolve())
