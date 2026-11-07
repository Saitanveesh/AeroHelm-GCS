"""Fail-closed exact-commit GitHub Actions gate for Cloud R2 promotion.

This is read-only: never merges, deploys, logs credentials or controls UAVs.
Runs on an operator workstation WITH HTTPS access to api.github.com.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from urllib import parse, request

REPO = "Saitanveesh/AeroHelm-GCS"
WORKFLOW = "AeroHelm Cloud Control Plane"


def verified_ci(sha: str, *, fetcher=None) -> bool:
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("Provide the exact lowercase 40-hex reviewed commit SHA")
    if fetcher is None:
        def fetcher(url):
            outgoing = request.Request(
                url, headers={
                    "Accept": "application/vnd.github+json",
                    "User-Agent": "aerohelm-cloud-release-gate",
                },
            )
            with request.urlopen(outgoing, timeout=8) as resp:
                return json.loads(resp.read(256_001))
    url = (f"https://api.github.com/repos/{REPO}/actions/runs"
           f"?head_sha={parse.quote(sha, safe='')}&per_page=50")
    body = fetcher(url)
    if not isinstance(body, dict) or not isinstance(body.get("workflow_runs"), list):
        return False
    for run in body["workflow_runs"]:
        if (run.get("head_sha") == sha and run.get("name") == WORKFLOW and
                run.get("status") == "completed" and
                run.get("conclusion") == "success"):
            return True
    return False


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sha", required=True)
    args = parser.parse_args(argv)
    try:
        passed = verified_ci(args.sha)
    except Exception as exc:
        print("RELEASE BLOCKED: exact-commit CI evidence unavailable:",
              type(exc).__name__)
        return 1
    if not passed:
        print("RELEASE BLOCKED: no completed successful cloud CI run "
              "for the exact reviewed commit SHA.")
        return 1
    print("CI evidence passed for the exact reviewed SHA; other "
          "infrastructure/aircraft safety gates remain separately required.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
