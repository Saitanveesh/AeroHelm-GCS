"""Regression: old runtime snapshots must never be compiled as GCS source.

This catches the Windows CS0101/CS0111 root cause without requiring an FC,
Windows build tools, or removing historical backup files.
"""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


class BuildGlobRegression(unittest.TestCase):
    def test_old_build_logs_are_removed_from_all_sdk_default_items(self):
        project = ET.parse(ROOT / "MissionPlanner.csproj")
        for item in ("Compile", "EmbeddedResource", "None"):
            removed = [
                elem.attrib.get("Remove", "").replace("/", "\\").lower()
                for elem in project.getroot().iter(item)
            ]
            self.assertIn(
                "build-logs\\**", removed,
                f"{item} defaults could include archived runtime files",
            )

    def test_future_snapshots_live_outside_the_repository(self):
        script = (ROOT / "scripts" / "Consolidate-AeroHelmWindows.ps1").read_text(
            encoding="utf-8-sig"
        )
        self.assertIn("AeroHelm-GCS-release-backups", script)
        self.assertIn("Split-Path -Parent $root", script)
        self.assertNotIn("$backup=Join-Path $logRoot ('release-backups", script)
        self.assertIn("Copy-Runtime $runtime $snapshot $false", script)
        self.assertIn("Copy-Runtime $snapshot $runtime $true", script)

    def test_no_destructive_cleanup_of_the_users_historical_snapshots(self):
        script = (ROOT / "scripts" / "Consolidate-AeroHelmWindows.ps1").read_text(
            encoding="utf-8-sig"
        )
        self.assertNotIn("git reset --hard", script)
        self.assertNotIn("git clean -fdx", script)
        self.assertNotIn("Remove-Item -LiteralPath $backupRoot", script)
        self.assertIn("Assert-PluginLayout", script)


if __name__ == "__main__":
    unittest.main()
