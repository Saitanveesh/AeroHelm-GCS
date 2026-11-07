"""Read-only Oracle cloud staging preflight (no deployment or credentials printed)."""
from __future__ import annotations

import os
from pathlib import Path
import re
import subprocess
import sys

CLOUD = Path(__file__).resolve().parents[2]
REQUIRED = (
    "db_admin_password.txt", "db_app_password.txt",
    "db_migrator_password.txt", "app_database_url.txt",
    "migrator_database_url.txt",
)


def check_files(directory: Path) -> list[str]:
    problems: list[str] = []
    if not directory.is_dir() or directory.is_symlink():
        return ["Missing or symlinked cloud secret directory"]
    for name in REQUIRED:
        path = directory / name
        if not path.is_file() or path.is_symlink():
            problems.append("Required secret file missing/unsafe: " + name)
            continue
        if path.stat().st_mode & 0o077:
            problems.append("Secret permissions too broad: " + name)
        if not path.read_bytes().strip():
            problems.append("Secret file empty: " + name)
    return problems


def check_env(env_path: Path) -> list[str]:
    if not env_path.is_file():
        return ["Production cloud/.env not provisioned"]
    values = {}
    for line in env_path.read_text(encoding="utf-8").splitlines():
        if line.lstrip().startswith("#") or "=" not in line:
            continue
        key, val = line.split("=", 1)
        values[key.strip()] = val.strip().strip('"')
    issues = []
    required = {
        "AEROHELM_CLOUD_MODE": "production",
        "AEROHELM_DATABASE_URL_FILE": "/run/secrets/app_database_url",
        "AEROHELM_VIRTUAL_DISPATCH_ENABLED": "false",
    }
    for key, expected in required.items():
        if values.get(key, "").lower() != expected.lower():
            issues.append("Unsafe deployment setting: " + key)
    for key in ("AEROHELM_OIDC_ISSUER", "AEROHELM_OIDC_JWKS_URL"):
        url = values.get(key, "")
        if not url.startswith("https://") or "example.invalid" in url:
            issues.append("Real HTTPS identity configuration missing: " + key)
    host = values.get("AEROHELM_ALLOWED_HOSTS", "")
    if (not host or "*" in host or "example.invalid" in host or
            "/" in host or ":" in host):
        issues.append("Exact private Tailscale Host allowlist not configured")
    if values.get("AEROHELM_TEST_JWT_SECRET"):
        issues.append("Test signing secret forbidden in production")
    if values.get("AEROHELM_DATABASE_URL"):
        issues.append("Inline database password not allowed; use secret file")
    return issues


def main() -> int:
    if sys.platform != "linux":
        print("Oracle staging preflight must run on the reviewed Linux host.")
        return 1
    problems = check_files(CLOUD / "secrets") + check_env(CLOUD / ".env")
    if not os.environ.get("COMPOSE_PROJECT_NAME"):
        problems.append("Set a unique COMPOSE_PROJECT_NAME to avoid old volumes")
    for command in (("docker", "compose", "version"), ("tailscale", "version")):
        try:
            result = subprocess.run(command, check=False, capture_output=True,
                                    text=True, timeout=5)
        except (FileNotFoundError, OSError, subprocess.TimeoutExpired):
            problems.append(command[0] + " not accessible")
            continue
        if result.returncode != 0:
            problems.append(command[0] + " not healthy")
    if not problems:
        try:
            result = subprocess.run([
                "docker", "compose", "-f", "cloud/compose.yaml",
                "-f", "cloud/compose.oci.yaml", "config", "--quiet",
            ], cwd=CLOUD.parent, capture_output=True, text=True,
                check=False, timeout=12)
            if result.returncode != 0:
                problems.append("Docker Compose configuration rejected")
        except (OSError, subprocess.TimeoutExpired):
            problems.append("Cannot validate private Docker Compose stack")
    for item in problems:
        print("BLOCKED:", item)
    if problems:
        print("Preflight did not deploy or change any existing service.")
        return 1
    print("PREFLIGHT PASS (configuration only, no remote/aircraft acceptance)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
