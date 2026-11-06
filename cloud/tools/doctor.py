"""AeroHelm Cloud verification entrypoint: tests only, never deploys or flies.

Commands:
    python -m cloud.tools.doctor --mode unit
    python -m cloud.tools.doctor --mode postgres  # disposable *_ci/_test DB
    python -m cloud.tools.doctor --mode image     # builds image only

This intentionally does NOT start Oracle/Pi/FC services or enable dispatch.
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess
import sys
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[2]


def call(argv: list[str], *, env=None) -> int:
    result = subprocess.run(argv, cwd=ROOT, env=env, check=False)
    return result.returncode


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("unit", "postgres", "image"),
                        default="unit")
    args = parser.parse_args(argv)
    if sys.version_info < (3, 12):
        parser.error("Cloud release verification requires Python 3.12+")
    print("AeroHelm cloud-only validation. NO aircraft commands or deployment.")
    if call([sys.executable, "-m", "compileall", "-q",
             "cloud/app", "cloud/tests", "cloud/tools", "cloud/migrations"]):
        return 1
    if args.mode == "unit":
        env = dict(os.environ)
        # Never let a local unit test accidentally use an operator's live DB.
        env.pop("AEROHELM_DATABASE_URL", None)
        env.pop("AEROHELM_DATABASE_URL_FILE", None)
        env["AEROHELM_CLOUD_MODE"] = "test"
        print("Unit tests use SQLite test fixtures. PostgreSQL tests may SKIP.")
        return call([sys.executable, "-m", "pytest", "-q", "cloud/tests",
                     "cloud/deploy/oci/test_staging_contract.py"], env=env)
    if args.mode == "postgres":
        url = os.getenv("AEROHELM_DATABASE_URL", "")
        if not url.startswith("postgresql+psycopg://"):
            parser.error("Require an explicit PostgreSQL test database URL")
        database = urlsplit(url).path.rsplit("/", 1)[-1]
        if not (database.endswith("_ci") or database.endswith("_test")):
            parser.error("Refusing to migrate/test a non-disposable database")
        if os.getenv("AEROHELM_CLOUD_MODE", "test").lower() == "production":
            parser.error("Refusing PostgreSQL tests in production mode")
        if call([sys.executable, "-m", "alembic", "-c", "cloud/alembic.ini",
                 "upgrade", "head"]):
            return 1
        return call([sys.executable, "-m", "pytest", "-q",
                     "cloud/tests/test_postgres_races.py",
                     "cloud/tests/test_analytics_postgres.py"])
    print("Building the cloud-only container; no deploy, no registry push.")
    return call(["docker", "build", "-f", "cloud/Dockerfile",
                 "-t", "aerohelm-cloud:test-local", "."])


if __name__ == "__main__":
    sys.exit(main())
