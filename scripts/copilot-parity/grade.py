"""Independent, deterministic acceptance checks; never grade the agent's self-report."""

import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def run_python(workspace, code=None, arguments=()):
    return subprocess.run(
        [sys.executable, *(["-c", code] if code is not None else arguments)],
        cwd=workspace, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
        env={**os.environ, "PYTHONIOENCODING": "utf-8", "PYTHONDONTWRITEBYTECODE": "1"},
    )


def grade(case, workspace, evidence, original_hashes, final_content):
    workspace, evidence = Path(workspace), Path(evidence)
    checks = {}
    details = {}

    def check(name, condition):
        checks[name] = bool(condition)

    def python_check(name, code):
        process = run_python(workspace, code)
        check(name, process.returncode == 0)
        details[name] = {"exitCode": process.returncode, "stdout": process.stdout, "stderr": process.stderr}

    try:
        protected = [p for p in original_hashes if p.startswith(("tests/", "input/", "sources/", "data/", ".github/")) or p.endswith("/AGENTS.md") or p in ("README.md", "requirements.md", "docs/contract.md", "AGENTS.md")]
        check("supplied_inputs_unchanged", all((workspace / p).is_file() and sha256(workspace / p) == original_hashes[p] for p in protected))
        if case == "nested-instructions":
            data = read_json(workspace / "nested/answer.json")
            check("correct_total", data.get("total") == 37)
            check("nested_instruction_applied", data.get("localPolicy") == "NESTED-POLICY-E4381")
        elif case == "instructions":
            data = read_json(workspace / "triage.json")
            check("correct_policy", data.get("policyCode") == "TRIAGE-2026-10-Q7")
            check("correct_classifications", data.get("classifications") == {"A": "critical", "B": "critical", "C": "high", "D": "normal", "E": "high"})
        elif case == "repair":
            python_check("holdout_range_contract", """
import copy
from src.ranges import merge_ranges
cases = [([], []), ([(10, 1), (12, 13)], [(1, 10), (12, 13)]),
    ([(2, 4), (1, 3), (9, 7)], [(1, 4), (7, 9)]),
    ([(1, 10), (2, 3), (4, 5)], [(1, 10)]),
    ([(1, 2), (3, 4)], [(1, 2), (3, 4)]),
    ([(1, 2), (2, 4)], [(1, 4)]),
    ([[-4, -2], [-3, 0], [8, 8], [8, 8]], [(-4, 0), (8, 8)]),
    ([(10**30, 10**30 + 1)], [(10**30, 10**30 + 1)])]
for source, expected in cases:
    before = copy.deepcopy(source)
    actual = merge_ranges(source)
    assert actual == expected, (source, actual, expected)
    assert source == before, 'input mutated'
    assert all(isinstance(item, tuple) for item in actual), 'not tuples'
print('8 holdout cases, including non-mutation and result-type checks, passed')
""")
            python_check("workspace_tests", "import unittest; r=unittest.TextTestRunner().run(unittest.defaultTestLoader.discover('tests')); raise SystemExit(not r.wasSuccessful())")
            check("added_regression_tests", any(p.relative_to(workspace).as_posix() not in original_hashes for p in workspace.rglob("test*.py")))
        elif case == "graph":
            python_check("holdout_graph_contract", """
import copy, json, subprocess, sys, tempfile
from pathlib import Path
from planner import layers
tasks = [{'id':'ship','dependsOn':['test','docs']},{'id':'test','dependsOn':['build','build']},{'id':'build'},{'id':'docs'},{'id':'unused'}]
before = copy.deepcopy(tasks)
assert layers(tasks) == [['build','docs','unused'],['test'],['ship']]
assert layers(list(reversed(tasks))) == layers(tasks)
assert layers(tasks, ['test']) == [['build'],['test']]
assert layers(tasks, ['test','test']) == [['build'],['test']]
assert layers(tasks, []) == []
assert tasks == before
assert layers([]) == []
bad = [([{'id':'x'},{'id':'x'}],None), ([{'id':'x','dependsOn':['absent']}],None),
       ([{'id':'x','dependsOn':['x']}],None),
       ([{'id':'x','dependsOn':['y']},{'id':'y','dependsOn':['x']},{'id':'ok'}],['ok']),
       ([{'id':'x','dependsOn':['absent']},{'id':'ok'}],['ok']),
       (tasks,['absent']), ([{'id':'x'},{'id':'x'}],[])]
for graph, targets in bad:
    try: layers(graph, targets)
    except ValueError: pass
    else: raise AssertionError(('expected ValueError', graph, targets))
with tempfile.TemporaryDirectory() as root:
    path = Path(root) / 'graph.json'
    path.write_text(json.dumps({'tasks':tasks}))
    p = subprocess.run([sys.executable,'-m','planner',str(path),'--target','test'],capture_output=True,text=True)
    assert p.returncode == 0 and json.loads(p.stdout) == {'layers':[['build'],['test']]}, (p.stdout,p.stderr)
    for data in ['{bad json', json.dumps({'tasks':[{'id':'x','dependsOn':['x']}]})]:
        path.write_text(data)
        p = subprocess.run([sys.executable,'-m','planner',str(path)],capture_output=True,text=True)
        assert p.returncode == 2 and p.stderr.strip() and 'Traceback' not in p.stderr and not p.stdout.strip(), (p.returncode,p.stdout,p.stderr)
    p = subprocess.run([sys.executable,'-m','planner',str(path)+'-missing'],capture_output=True,text=True)
    assert p.returncode == 2 and p.stderr.strip() and 'Traceback' not in p.stderr and not p.stdout.strip()
print('Graph closure, order, validation, mutation and CLI holdout checks passed')
""")
            python_check("workspace_tests", "import unittest; r=unittest.TextTestRunner().run(unittest.defaultTestLoader.discover('tests')); raise SystemExit(not r.wasSuccessful())")
            plan = (workspace / "PLAN.md").read_text(encoding="utf-8-sig")
            check("plan_with_verification", "test" in plan.lower() and any(word in plan.lower() for word in ("pass", "verified", "complete", "[x]")))
            check("added_regression_tests", any(p.relative_to(workspace).as_posix() not in original_hashes for p in workspace.rglob("test*.py")))
        elif case == "analysis":
            data = read_json(workspace / "decision.json")
            check("recommendation", data.get("recommendation") == "Atlas")
            check("mandatory_cost", data.get("firstYearCost") == 11900)
            rejected = data.get("rejected", {})
            check("rejection_reasons", {k: sorted(v) for k, v in rejected.items()} == {"Boreal": ["residency"], "Cedar": ["budget"]})
            check("budget_sensitivity", data.get("atBudget11500") == "none")
            report = (workspace / "report.md").read_text(encoding="utf-8-sig")
            check("cites_decisive_sources", all(name in report for name in ("atlas-support-2026-10-09.md", "boreal-dpa-2026-10-15.md", "cedar-quote-2026-10-16.md")))
            check("acknowledges_unverified_latency", any(word in report.lower() for word in ("unverified", "independent", "not verified", "not independently")))
        elif case == "automation":
            python_check("holdout_archive_contract", """
import hashlib, json, subprocess, sys, tempfile
from pathlib import Path
script = str(Path('archive.py').resolve())
with tempfile.TemporaryDirectory() as root:
    source, dest = Path(root)/'source', Path(root)/'destination'
    source.mkdir()
    (source/'nested').mkdir()
    blobs = {'a b.txt':b'hello', 'nested/café.bin':bytes(range(256)), 'copy.txt':b'hello', 'empty':b'', 'skip.tmp':b'excluded'}
    for name, blob in blobs.items(): (source/name).write_bytes(blob)
    expected = {'files':[{'path':name,'sha256':hashlib.sha256(blob).hexdigest(),'bytes':len(blob)} for name,blob in sorted(blobs.items()) if not name.endswith('.tmp')]}
    def invoke(*extra):
        p = subprocess.run([sys.executable,script,str(source),str(dest),*extra],capture_output=True,text=True,encoding='utf-8')
        assert p.returncode == 0, (p.returncode,p.stderr)
        assert json.loads(p.stdout) == expected, p.stdout
    invoke('--dry-run')
    assert not dest.exists(), 'dry-run wrote destination'
    invoke()
    assert json.loads((dest/'manifest.json').read_text(encoding='utf-8-sig')) == expected
    actual = {p.relative_to(dest).as_posix():p.read_bytes() for p in dest.rglob('*') if p.is_file()}
    expected_names = {'manifest.json'} | {'objects/'+row['sha256']+'.bin' for row in expected['files']}
    assert set(actual) == expected_names, set(actual)
    for name,blob in blobs.items():
        assert (source/name).read_bytes() == blob
        if not name.endswith('.tmp'): assert (dest/'objects'/(hashlib.sha256(blob).hexdigest()+'.bin')).read_bytes() == blob
    invoke()
    assert actual == {p.relative_to(dest).as_posix():p.read_bytes() for p in dest.rglob('*') if p.is_file()}
    empty = Path(root)/'empty'
    empty.mkdir()
    p = subprocess.run([sys.executable,script,str(empty),str(Path(root)/'empty-output'),'--dry-run'],capture_output=True,text=True)
    assert p.returncode == 0 and json.loads(p.stdout) == {'files':[]}
print('Dry-run, binary/Unicode, deduplication, idempotency, non-mutation and empty-tree checks passed')
""")
            check("verification_record", (workspace / "verification.md").is_file())
            check("added_tests", any(workspace.rglob("test*.py")))
        elif case == "mcp-recovery":
            data = read_json(workspace / "invoice.json")
            expected = {"sourceAmount": 240, "sourceCurrency": "EUR", "targetCurrency": "USD", "rate": 1.125, "targetAmount": 270, "receipt": "rate-eu-42"}
            check("invoice", all(data.get(k) == v for k, v in expected.items()))
            calls = [json.loads(line) for line in (evidence / "mcp-calls.jsonl").read_text(encoding="utf-8").splitlines()]
            check("real_mcp_error_then_recovery", len(calls) >= 2 and calls[0]["isError"] and any(not call["isError"] for call in calls[1:]))
    except Exception as error:
        check("grader_completed", False)
        details["graderError"] = f"{type(error).__name__}: {error}"

    return {
        "passed": bool(checks) and all(checks.values()),
        "checks": checks,
        "instructions": {
            "agents_md": "PROJECT_CANARY_6C21" in (final_content or ""),
            "copilot_instructions_md": "SOURCE_POLICY_74B9" in (final_content or ""),
        },
        "details": details,
    }
