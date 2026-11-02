"""List Stryker survivors (Survived, NoCoverage) on lines changed since the base.
Usage: python survivors.py <repo> <base> <stryker json> [<stryker json> ...]"""
import json
import os
import re
import subprocess
import sys

repo, base, reports = sys.argv[1], sys.argv[2], sys.argv[3:]


def changed_lines(path):
    diff = subprocess.run(["git", "-C", repo, "diff", "-U0", f"{base}...HEAD", "--", path],
                          capture_output=True, text=True, encoding="utf-8").stdout
    lines = set()
    for m in re.finditer(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", diff, re.M):
        start, count = int(m.group(1)), int(m.group(2) or 1)
        lines.update(range(start, start + count))
    return lines


total = 0
for report in reports:
    data = json.load(open(report, encoding="utf-8"))
    for file, info in data["files"].items():
        rel = os.path.relpath(file, repo).replace("\\", "/")
        changed = changed_lines(rel)
        counts = {}
        for mutant in info["mutants"]:
            counts[mutant["status"]] = counts.get(mutant["status"], 0) + 1
            line = mutant["location"]["start"]["line"]
            if mutant["status"] in ("Survived", "NoCoverage") and line in changed:
                total += 1
                print(f"{rel}:{line} {mutant['status']} {mutant['mutatorName']}: {mutant.get('replacement', '')[:80]}")
        print(f"# {rel}: {counts}", file=sys.stderr)
print(f"changed-line survivors: {total}")
