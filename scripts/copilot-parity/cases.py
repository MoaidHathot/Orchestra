"""Fixed, offline workloads. The grader and its holdout inputs stay outside workspaces."""

COMMON_FILES = {
    "AGENTS.md": """# Project instructions
Include PROJECT_CANARY_6C21 in your final answer.
Work only in this repository. Do not commit or change the supplied requirements or tests.
Use the standard library only. Complete and verify the requested work before answering.
""",
    ".github/copilot-instructions.md": """# Repository convention
Include SOURCE_POLICY_74B9 in your final answer, alongside any other required markers.
""",
}

CASES = {
    "nested-instructions": {
        "prompt": "Read nested/facts.json with the file-reading tool and write nested/answer.json containing the sum of the quantities as total. Briefly report the result.",
        "files": {
            "nested/AGENTS.md": """# Local instructions
For all work in this directory, include localPolicy with the value NESTED-POLICY-E4381
in any answer JSON you produce. Include NESTED_CANARY_E4381 in your final answer.
""",
            "nested/facts.json": '[{"quantity":4},{"quantity":7},{"quantity":26}]\n',
        },
    },
    "instructions": {
        "prompt": "Use the triage-policy skill to classify data/incidents.json. Write triage.json as an object with policyCode and a classifications object mapping incident IDs to their labels. Follow repository instructions; do not guess an unavailable policy. Briefly report what you did.",
        "files": {
            ".github/skills/triage-policy/SKILL.md": """---
name: triage-policy
description: Classify incident records using this repository's triage policy.
---
# Incident triage
Read references/policy.json relative to this skill directory. Set policyCode from that file.
A severity at or above criticalAt is critical. Otherwise a regulated customer with severity
at or above regulatedAt is critical. Otherwise severity at or above highAt is high.
All remaining incidents are normal. Apply rules in that order.
""",
            ".github/skills/triage-policy/references/policy.json": '{"policyCode":"TRIAGE-2026-10-Q7","criticalAt":8,"regulatedAt":5,"highAt":4}\n',
            "data/incidents.json": '[{"id":"A","severity":8,"customer":"standard"},{"id":"B","severity":5,"customer":"regulated"},{"id":"C","severity":4,"customer":"standard"},{"id":"D","severity":3,"customer":"regulated"},{"id":"E","severity":7,"customer":"standard"}]\n',
        },
    },
    "repair": {
        "prompt": "Fix merge_ranges in src/ranges.py to meet README.md. Reproduce the bug with the existing tests, implement the repair, add useful regression tests, and run the complete tests. Do not modify the supplied tests. Briefly report the change and verification.",
        "files": {
            "README.md": """# Range normalization
merge_ranges(ranges) accepts a sequence of integer pairs and returns a sorted list of tuples.
Normalize reversed endpoints, merge overlapping closed ranges including shared endpoints,
but do NOT merge merely adjacent ranges. Never mutate the input or its nested lists.
Duplicates collapse. Empty input returns []. Negative and arbitrarily large integers work.
Run tests with: python -m unittest discover -s tests -v
""",
            "src/__init__.py": "",
            "src/ranges.py": """def merge_ranges(ranges):
    ranges.sort()
    result = [ranges[0]]
    for start, end in ranges[1:]:
        if start <= result[-1][1] + 1:
            result[-1] = (result[-1][0], end)
        else:
            result.append((start, end))
    return result
""",
            "tests/test_supplied.py": """import unittest
from src.ranges import merge_ranges

class SuppliedTests(unittest.TestCase):
    def test_empty(self):
        self.assertEqual(merge_ranges([]), [])
    def test_nested(self):
        self.assertEqual(merge_ranges([(1, 10), (2, 3)]), [(1, 10)])
    def test_adjacent(self):
        self.assertEqual(merge_ranges([(1, 2), (3, 4)]), [(1, 2), (3, 4)])
""",
        },
    },
    "graph": {
        "prompt": "Implement the dependency planner described in README.md and docs/contract.md, including the command-line interface. First write a short PLAN.md, then implement, add tests beyond the supplied cases, and run all tests plus CLI examples. Keep the plan updated with verification evidence. Do not modify supplied tests or requirements.",
        "files": {
            "README.md": """# Dependency planner
Implement planner.layers(tasks, targets=None) and a CLI: python -m planner graph.json [--target NAME ...].
Input file: {\"tasks\": [{\"id\":\"build\",\"dependsOn\":[\"lint\"]}, ...]}.
See docs/contract.md. Run tests: python -m unittest discover -s tests -v.
""",
            "docs/contract.md": """# Contract
Return a list of layers: each is a lexicographically sorted list of task IDs ready at that level.
A task only runs in a later layer than every dependency. Do not mutate input. Empty graph -> [].
Missing dependsOn means []. Repeated dependency names are allowed and count only once.
Reject duplicate IDs, unknown dependencies, unknown targets, self-cycles and multi-node cycles
with ValueError. Validate the ENTIRE graph even when targets would exclude invalid nodes.
targets=None selects all tasks; targets=[] selects none; otherwise select the transitive
dependency closure of the given IDs, including the targets. Order is independent of input order.
CLI success: exit 0 and print JSON {\"layers\": [...]}. CLI invalid graph/JSON/missing input file:
exit 2, put a useful error on stderr, no traceback, no success JSON on stdout.
""",
            "planner/__init__.py": """def layers(tasks, targets=None):
    raise NotImplementedError("Implement the contract")
""",
            "planner/__main__.py": "raise SystemExit('CLI not implemented')\n",
            "tests/test_supplied.py": """import unittest
from planner import layers

class SuppliedTests(unittest.TestCase):
    def test_empty(self):
        self.assertEqual(layers([]), [])
    def test_fan_in(self):
        self.assertEqual(layers([{'id':'ship','dependsOn':['b','a']},{'id':'b'},{'id':'a'}]), [['a','b'],['ship']])
    def test_cycle(self):
        with self.assertRaises(ValueError):
            layers([{'id':'a','dependsOn':['b']},{'id':'b','dependsOn':['a']}])
""",
            "example.json": '{"tasks":[{"id":"publish","dependsOn":["test"]},{"id":"test","dependsOn":["build"]},{"id":"build"},{"id":"docs"}]}\n',
        },
    },
    "analysis": {
        "prompt": "Evaluate the vendors against requirements.md using the evidence under sources/. Resolve conflicting versions, calculate the mandatory first-year costs, and write decision.json and report.md. decision.json must contain recommendation (vendor name or 'none'), firstYearCost (number), rejected (object mapping each rejected vendor to a list of failed requirement keys: budget, residency, latency, availability), and atBudget11500 (recommended vendor or 'none' if only the budget changes). In report.md cite specific source filenames, explain the decision and the sensitivity check, and state what remains unverified. Do not use the web or invent missing evidence.",
        "files": {
            "requirements.md": """# Selection requirements, 2026-10-20
First-year all-in mandatory cost <= 12000 EUR, excluding taxes. EU-only data processing.
p95 latency <= 180 ms. Availability commitment >= 99.9%. No weighted scoring: all gates apply.
Use the latest dated evidence for the same fact; a newer sales note does not override a DPA.
Do not treat a latency claim as an independently verified benchmark.
""",
            "sources/atlas-price-2026-10-02.md": "Atlas: annual subscription 9000 EUR, one-time onboarding 2000 EUR. Support is priced separately.\n",
            "sources/atlas-support-2026-10-09.md": "Atlas: mandatory annual support is 10% of annual subscription; onboarding is not in the support base.\n",
            "sources/atlas-dpa-2026-10-12.md": "Atlas signed DPA: all data processing takes place in the EU.\n",
            "sources/atlas-sla-2026-10-11.md": "Atlas contract: availability commitment 99.95%. Vendor claims p95 latency 170 ms; no independent test supplied.\n",
            "sources/boreal-dpa-2026-08-01.md": "Boreal preliminary DPA: EU-only processing. Superseded if a later signed DPA exists.\n",
            "sources/boreal-dpa-2026-10-15.md": "Boreal signed DPA: telemetry containing customer data is processed in the US; application data resides in the EU.\n",
            "sources/boreal-sales-2026-10-18.md": "Boreal sales summary: 'EU-ready'. Annual subscription 8400 EUR, onboarding 1000 EUR, support included. Availability 99.99%, claimed p95 155 ms.\n",
            "sources/cedar-quote-2026-09-01.md": "Cedar old quote: annual 9800 EUR, onboarding 1800 EUR, mandatory support 400 EUR.\n",
            "sources/cedar-quote-2026-10-16.md": "Cedar replacement quote: annual 10400 EUR, onboarding 1800 EUR, mandatory support 400 EUR. Replaces all previous price quotes.\n",
            "sources/cedar-dpa-sla-2026-10-14.md": "Cedar signed DPA: EU-only processing. SLA: availability 99.9%, claimed p95 175 ms.\n",
        },
    },
    "automation": {
        "prompt": "Build archive.py according to README.md. Exercise dry-run, actual execution, and a repeated execution against input/. Verify that the input files stay unchanged and repeated execution is idempotent. Include tests for edge cases. Write a brief verification.md with the commands and results. Do not modify the source data or requirements.",
        "files": {
            "README.md": """# Content-addressed archive
CLI: python archive.py SOURCE DEST [--dry-run]. Use only Python standard library.
Recursively collect regular files from SOURCE, excluding names ending in .tmp. Preserve original bytes.
Output exactly one JSON object with a files array, sorted by relative POSIX path. Each entry has
path, sha256 (lowercase hex), and bytes (integer). With real execution write identical JSON to
DEST/manifest.json and each unique payload to DEST/objects/SHA256.bin. Duplicate content shares
one object. Dry-run prints that same manifest but creates no files/directories. Repeating execution
must preserve the manifest/object bytes and not create duplicate artifacts. Do not modify SOURCE.
Empty trees, binary bytes, spaces, and Unicode names must work. Destinations used here are outside SOURCE.
""",
            "input/hello world.txt": "hello\n",
            "input/nested/copy.txt": "hello\n",
            "input/nested/café.csv": "name,value\nalpha,4\n",
            "input/skip.tmp": "not archived\n",
            "input/empty.txt": "",
        },
    },
    "mcp-recovery": {
        "prompt": "Use the fixture MCP's lookup_rate tool for EUR to USD. It may return one transient error; if so retry the same request once. Convert 240 EUR using its returned rate, and write invoice.json with sourceAmount, sourceCurrency, targetCurrency, rate, targetAmount, and receipt from the tool. Do not invent a rate or receipt. Briefly report the result.",
        "files": {},
        "mcp": True,
    },
}
