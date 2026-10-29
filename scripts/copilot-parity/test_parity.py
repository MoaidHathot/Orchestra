"""Offline checks for false-positive grading and the real MCP fixture protocol."""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from cases import CASES, COMMON_FILES
from grade import grade, sha256
from run import summarize_events
from analyze import git_audit


class ParityTests(unittest.TestCase):
    def setUp(self):
        root = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "Temp/opencode"
        root.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="parity-grader-", dir=root)
        self.root = Path(self.temp.name)
        self.workspace = self.root / "workspace"
        self.evidence = self.root / "evidence"
        self.workspace.mkdir()
        self.evidence.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def prepare(self, case):
        for name, content in {**COMMON_FILES, **CASES[case]["files"]}.items():
            path = self.workspace / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8", newline="\n")
        return {p.relative_to(self.workspace).as_posix(): sha256(p) for p in self.workspace.rglob("*") if p.is_file()}

    def test_self_report_without_artifact_is_not_success(self):
        original = self.prepare("instructions")
        result = grade("instructions", self.workspace, self.evidence, original, "Done! PROJECT_CANARY_6C21 SOURCE_POLICY_74B9")
        self.assertFalse(result["passed"])
        self.assertTrue(all(result["instructions"].values()))

    def test_correct_artifact_and_instruction_compliance_are_separate(self):
        original = self.prepare("instructions")
        (self.workspace / "triage.json").write_text(json.dumps({"policyCode": "TRIAGE-2026-10-Q7", "classifications": {"A": "critical", "B": "critical", "C": "high", "D": "normal", "E": "high"}}))
        result = grade("instructions", self.workspace, self.evidence, original, "Done")
        self.assertTrue(result["passed"])
        self.assertFalse(any(result["instructions"].values()))
        (self.workspace / "data/incidents.json").write_text("[]")
        result = grade("instructions", self.workspace, self.evidence, original, "Done")
        self.assertFalse(result["passed"])
        self.assertFalse(result["checks"]["supplied_inputs_unchanged"])

    def test_holdout_rejects_the_supplied_buggy_code(self):
        original = self.prepare("repair")
        result = grade("repair", self.workspace, self.evidence, original, "All tests passed")
        self.assertFalse(result["passed"])
        self.assertFalse(result["checks"]["holdout_range_contract"])
        self.assertFalse(result["checks"]["workspace_tests"])

    def test_correct_sum_does_not_mask_missing_nested_instructions(self):
        original = self.prepare("nested-instructions")
        path = self.workspace / "nested/answer.json"
        path.write_text('{"total":37}')
        result = grade("nested-instructions", self.workspace, self.evidence, original, "Done")
        self.assertTrue(result["checks"]["correct_total"])
        self.assertFalse(result["passed"])
        path.write_text('{"total":37,"localPolicy":"NESTED-POLICY-E4381"}')
        self.assertTrue(grade("nested-instructions", self.workspace, self.evidence, original, "Done")["passed"])

    def test_holdout_accepts_correct_ranges_with_regressions(self):
        original = self.prepare("repair")
        (self.workspace / "src/ranges.py").write_text("""def merge_ranges(ranges):
    result = []
    for start, end in sorted((min(pair), max(pair)) for pair in ranges):
        if result and start <= result[-1][1]:
            result[-1] = (result[-1][0], max(end, result[-1][1]))
        else:
            result.append((start, end))
    return result
""")
        (self.workspace / "tests/test_new.py").write_text("""import unittest
from src.ranges import merge_ranges
class Regression(unittest.TestCase):
    def test_reverse(self):
        self.assertEqual(merge_ranges([(8, 2)]), [(2, 8)])
""")
        result = grade("repair", self.workspace, self.evidence, original, "Done")
        self.assertTrue(result["passed"], result)

    def test_mcp_fixture_really_errors_then_recovers(self):
        messages = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-03-26"}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
            *[{"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": {"name": "lookup_rate", "arguments": {"base": "EUR", "quote": "USD"}}} for i in (3, 4)],
        ]
        log = self.evidence / "mcp-calls.jsonl"
        response = subprocess.run([sys.executable, str(Path(__file__).with_name("fixture_mcp.py")), str(log)], input="\n".join(json.dumps(m) for m in messages) + "\n", text=True, capture_output=True, timeout=10)
        self.assertEqual(response.returncode, 0, response.stderr)
        replies = [json.loads(line) for line in response.stdout.splitlines()]
        self.assertEqual([r["id"] for r in replies], [1, 2, 3, 4])
        self.assertTrue(replies[2]["result"]["isError"])
        self.assertFalse(replies[3]["result"]["isError"])
        self.assertEqual(json.loads(replies[3]["result"]["content"][0]["text"])["receipt"], "rate-eu-42")
        original = self.prepare("mcp-recovery")
        (self.workspace / "invoice.json").write_text(json.dumps({"sourceAmount": 240, "sourceCurrency": "EUR", "targetCurrency": "USD", "rate": 1.125, "targetAmount": 270, "receipt": "rate-eu-42"}))
        self.assertTrue(grade("mcp-recovery", self.workspace, self.evidence, original, "Done")["passed"])
        log.unlink()
        self.assertFalse(grade("mcp-recovery", self.workspace, self.evidence, original, "Done")["passed"])

    def test_summary_does_not_confuse_subagent_reply_with_final_answer(self):
        summary = summarize_events([
            {"type": "assistant.message", "data": {"content": "root answer"}},
            {"type": "assistant.message", "agentId": "child", "data": {"content": "child answer"}},
            {"type": "tool.execution_start", "data": {"toolName": "view"}},
            {"type": "tool.execution_complete", "data": {"success": False}},
            {"type": "assistant.usage", "data": {"model": "test-model", "inputTokens": 12, "outputTokens": 3}},
        ])
        self.assertEqual(summary["finalContent"], "root answer")
        self.assertEqual(summary["tools"], {"view": 1})
        self.assertEqual(summary["failedToolCalls"], 1)
        self.assertEqual(summary["models"], ["test-model"])

    def test_git_audit_detects_commits_independently_of_agent_claims(self):
        subprocess.run(["git", "init", "--quiet", str(self.workspace)], check=True, capture_output=True)
        self.assertTrue(git_audit(self.workspace)["noCommitInstructionSatisfied"])
        (self.workspace / "file.txt").write_text("fixture")
        subprocess.run(["git", "-C", str(self.workspace), "add", "file.txt"], check=True, capture_output=True)
        subprocess.run(["git", "-C", str(self.workspace), "-c", "user.name=Parity Test", "-c", "user.email=parity@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Fixture commit"], check=True, capture_output=True)
        audit = git_audit(self.workspace)
        self.assertFalse(audit["noCommitInstructionSatisfied"])
        self.assertEqual(audit["finalCommitCount"], 1)
        self.assertEqual(len(audit["commitIds"][0]), 40)


if __name__ == "__main__":
    unittest.main()
