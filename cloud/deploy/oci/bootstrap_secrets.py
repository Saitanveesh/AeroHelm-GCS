"""One-time local-only bootstrap for a NEW Cloud R2 PostgreSQL volume.

Generates random secrets; never sends credentials to GitHub or to an aircraft.
Does not overwrite existing secrets. Run on the operator's Oracle host ONLY
after reviewing storage permissions, PostgreSQL data volume and backup policy.
"""
from __future__ import annotations

import os
from pathlib import Path
import secrets
from urllib.parse import quote

ROOT = Path(__file__).resolve().parents[2]
SECRET_DIR = ROOT / "secrets"


def generate() -> None:
    if SECRET_DIR.is_symlink():
        raise SystemExit("Refusing symlinked secret directory")
    SECRET_DIR.mkdir(mode=0o700, exist_ok=True)
    os.chmod(SECRET_DIR, 0o700)
    names = (
        "db_admin_password.txt", "db_app_password.txt",
        "db_migrator_password.txt", "app_database_url.txt",
        "migrator_database_url.txt",
    )
    if any((SECRET_DIR / name).exists() or (SECRET_DIR / name).is_symlink()
           for name in names):
        raise SystemExit("Existing cloud secrets detected; refusing overwrite")

    admin_pw, app_pw, migration_pw = (secrets.token_urlsafe(48) for _ in range(3))
    payloads = {
        "db_admin_password.txt": admin_pw + "\n",
        "db_app_password.txt": app_pw + "\n",
        "db_migrator_password.txt": migration_pw + "\n",
        "app_database_url.txt": (
            "postgresql+psycopg://aerohelm_app:" +
            quote(app_pw, safe="") + "@database:5432/aerohelm\n"),
        "migrator_database_url.txt": (
            "postgresql+psycopg://aerohelm_migrator:" +
            quote(migration_pw, safe="") + "@database:5432/aerohelm\n"),
    }
    created = []
    try:
        for name in names:
            path = SECRET_DIR / name
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            created.append(path)
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                handle.write(payloads[name])
    except BaseException:
        for path in created:
            path.unlink(missing_ok=True)
        raise
    print("Created restricted cloud credentials; values not displayed.")
    print("Provision an encrypted off-host backup before deployment.")


if __name__ == "__main__":
    generate()
