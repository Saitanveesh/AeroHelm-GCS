"""Offline source contracts for the Vihanga Mission Planner toolbar.

No cloud or aircraft connections are opened. Native Windows builds and
bench acceptance must be performed separately.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


class VihangaNativeToolbarContracts(unittest.TestCase):
    def test_retired_touch_panel_is_not_compiled(self):
        self.assertFalse((ROOT / "plugins/AeroHelmTouchPanel.cs").exists())
        cloud = source("plugins/AeroHelmCloud.cs")
        project = source("Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj")
        self.assertNotIn("AeroHelmTouchPanel", cloud)
        self.assertNotIn("AEROHELM_TOUCH_STARTUP", cloud)
        self.assertNotIn("AeroHelmTouchPanel.cs", project)
        self.assertIn("VihangaSharedUi.cs", project)
        self.assertIn("internal sealed class VihangaSettingsWindow", source("plugins/VihangaSharedUi.cs"))

    def test_native_toolbar_and_missing_workspaces_are_exposed(self):
        cloud = source("plugins/AeroHelmCloud.cs")
        self.assertIn('return "Vihanga Ground Station";', cloud)
        for name in ("SURVEY", "VISION", "LINK", "OPS", "INTEL", "PATHS"):
            self.assertIn(f'NewToolbarButton("{name}"', cloud)
        self.assertIn('new ToolStripButton("CLOUD")', cloud)
        self.assertIn('new ToolStripDropDownButton("MORE")', cloud)
        self.assertIn('menu.Items.Insert(menu.Items.IndexOf(_button) + 1, _paths)', cloud)
        for name in ("FLEET", "SECURITY", "ANALYTICS"):
            self.assertIn(f'new AeroHelmReadOnlyPage("{name}")', cloud)
        self.assertIn('new AeroHelmIntelWindow("PARAMETERS")', cloud)
        self.assertIn('new VihangaSettingsWindow()', cloud)
        self.assertIn('main.MenuConfigTune.PerformClick()', source("plugins/VihangaSharedUi.cs"))

    def test_cloud_view_does_not_automatically_take_pilot_radio(self):
        cloud = source("plugins/AeroHelmCloud.cs")
        selector = source("plugins/AeroHelmLinkSelector.cs")
        direct = source("plugins/AeroHelmPilotDirectWindow.cs")
        self.assertIn('new AeroHelmLinkSelector()', cloud)
        self.assertIn('AeroHelmLinkNavigation.Open(MainV2.instance,', cloud)
        self.assertIn("NO AUTO-FAILOVER", selector)
        self.assertIn("PILOT BACKUP", selector)
        self.assertIn("main.doConnect(MainV2.comPort,port,baud,true,true)", direct)
        self.assertIn("MessageBoxButtons.YesNo", direct)
        self.assertIn('"GET","api/v1/mavlink/health"', cloud)
        watchdog = cloud.split("private async Task CheckCloudLinkAsync()", 1)[1].split(
            "private static void ShowWorkspace", 1)[0]
        for denied in ("main.doConnect", "ws.SendAsync", "MAV_CMD", "PARAM_SET"):
            self.assertNotIn(denied, watchdog)

    def test_ops_intel_have_live_authenticated_endpoints(self):
        ops = source("plugins/AeroHelmOperations.cs")
        intel = source("plugins/AeroHelmPlatformV2.cs")
        self.assertIn('Api("GET","api/v1/operations/capabilities")', ops)
        self.assertIn('"api/v1/operations/missions"', ops)
        self.assertIn('"api/v1/operations/calibrations"', ops)
        self.assertIn('"api/v2/fleet"', intel)
        self.assertIn('"api/v2/security/posture"', intel)
        self.assertIn('"api/v1/vehicles/"+Id()+"/parameters"', intel)
        self.assertIn('vehicle.Text=""', intel)

    def test_mission_validation_is_invalidated_after_edits(self):
        intel = source("plugins/AeroHelmPlatformV2.cs")
        for event in ("points.TextChanged", "fence.TextChanged",
                      "altitude.ValueChanged", "speed.ValueChanged"):
            self.assertIn(event, intel)
        self.assertIn("InvalidateMissionValidation()", intel)
        self.assertIn("lastMissionPlan", intel)
        self.assertIn("Plan changed during validation", intel)
        self.assertIn("Revalidate before starting a passive shadow", intel)
        self.assertIn("new SortedDictionary<string,double>(StringComparer.Ordinal)", intel)
        self.assertIn("Duplicate parameter:", intel)

    def test_release_pins_source_and_native_binary_hashes(self):
        launcher = source("scripts/Start-VihangaGCS.ps1")
        release = source("scripts/Consolidate-AeroHelmWindows.ps1")
        session = source("scripts/Start-AeroHelm-Session.ps1")
        for key in ("release.git_commit", "release.executable_sha256",
                    "release.plugin_sha256", "Get-NetTCPConnection -LocalPort 5760",
                    "AEROHELM_CLOUD_READONLY", "LocalDirectBackup"):
            self.assertIn(key, launcher)
        self.assertIn("feat/vihanga-phase3-native-toolbar", launcher)
        self.assertIn("Copy-Runtime $runtime $snapshot $false", release)
        self.assertIn("Copy-Runtime $snapshot $runtime $true", release)
        self.assertIn("Start-VihangaGCS.ps1", session)
        self.assertNotIn("AEROHELM_TOUCH_STARTUP", launcher)
        self.assertIn("Start-VihangaGCS.ps1", source("scripts/Start-AeroHelm-Touch.ps1"))

    def test_cloud_fleet_unknowns_are_not_flight_authority(self):
        pages = source("plugins/AeroHelmReadOnlyPages.cs")
        for marker in ("TELEMETRY STALE/UNAVAILABLE", "SIMULATED / FRESH",
                       "Kerberos integration: NOT PROVISIONED", "cloud_flight_authorized"):
            self.assertIn(marker, pages)
        self.assertNotIn('OperationsApi.Request("POST","api/v1/device', pages)


if __name__ == "__main__":
    unittest.main()
