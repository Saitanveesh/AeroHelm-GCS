"""Native toolbar and virtual cloud-operations source regression contracts.

These tests inspect source files only. They never open Windows ports, contact
cloud infrastructure, issue MAVLink commands, or launch a simulator.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


class VihangaToolbarContracts(unittest.TestCase):
    def test_name_and_native_toolbar_replace_touch(self):
        plugin = source("plugins/AeroHelmCloud.cs")
        presentation = source("plugins/VihangaVedikaPresentation.cs")
        project = source("Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj")
        self.assertIn('internal const string FullName = "Vihanga Vedika Ground Control Station"', presentation)
        self.assertIn('main.Text = VihangaVedikaProduct.FullName', plugin)
        self.assertIn('VihangaVedikaProduct.FullName', plugin)
        for button in ("SURVEY", "VISION", "LINK", "FLEET", "OPS", "INTEL", "PATHS"):
            self.assertIn(f'NewToolbarButton("{button}"', plugin)
        self.assertIn('new CloudWindow()', plugin)
        self.assertIn('new AeroHelmIntelWindow("FLEET")', plugin)
        for old in ('NewToolbarButton("TOUCH"', 'AeroHelmTouchPanel',
                    'AEROHELM_TOUCH_STARTUP'):
            self.assertNotIn(old, plugin)
        for retired in ("AeroHelmTouchPanel.cs", "AeroHelmReadOnlyPages.cs",
                        "AeroHelmTwinLabWindow.cs", "AeroHelmFleetLabWindow.cs"):
            self.assertNotIn(retired, project)
        for needed in ("VihangaVedikaPresentation.cs", "VihangaVedikaCloudR2.cs"):
            self.assertIn(needed, project)

    def test_direct_pilot_radio_is_never_automatically_switched(self):
        toolbar = source("plugins/AeroHelmCloud.cs")
        chooser = source("plugins/AeroHelmLinkSelector.cs")
        direct = source("plugins/AeroHelmPilotDirectWindow.cs")
        self.assertIn('new AeroHelmLinkSelector()', toolbar)
        self.assertIn('NO automatic failover', toolbar)
        self.assertIn('AeroHelm will not change control authority automatically.', chooser)
        self.assertIn('MainV2.comPort.BaseStream.IsOpen', chooser)
        self.assertIn('MessageBoxButtons.YesNo', direct)
        self.assertIn('SerialPort.GetPortNames()', direct)
        self.assertNotIn('ws.SendAsync(', chooser)

    def test_virtual_ops_api_never_exposes_physical_flight(self):
        frontend = source("plugins/VihangaVedikaCloudR2.cs")
        ops = source("plugins/AeroHelmOperations.cs")
        self.assertIn('class VihangaVirtualOpsWindow', frontend)
        for expected in ('VIRTUAL_SIMULATOR_ONLY', 'physical_flight_authorized',
                         'virtual_dispatch_enabled', 'APPROVED_SIM',
                         'api/v1/missions', 'api/v1/vehicles/',
                         '/approve', '/lease/renew', '/start', '/abort'):
            self.assertIn(expected, frontend)
        self.assertIn('MainV2.instance', ops)
        self.assertIn('main.MenuSimulation.PerformClick()', ops)
        self.assertIn('new VihangaVirtualOpsWindow()', ops)
        for forbidden in ('command_long_send(', 'param_set_send(', 'ws.SendAsync(',
                          'mavlink_connection(', 'Serial('):
            self.assertNotIn(forbidden, frontend)

    def test_intelligence_is_cloud_aggregated_and_read_only(self):
        intel = source("plugins/AeroHelmPlatformV2.cs")
        for route in ('api/v1/fleet/status', 'api/v1/fleet/metrics',
                      'api/v1/missions?limit=50', 'api/v1/events?limit=50',
                      'api/v1/cloud-compute/jobs'):
            self.assertIn(route, intel)
        self.assertIn('cloud_flight_authorized', intel)
        self.assertIn('rows.Length>128', intel)
        self.assertIn('fresh?"FRESH":"STALE"', intel)
        self.assertIn('DEVICE-REPORTED', intel)
        self.assertIn('SIMULATED', intel)
        self.assertIn('No local fleet aggregation', intel)
        self.assertNotIn('command_long_send', intel)

    def test_parameter_comparison_is_ordered_and_duplicate_safe(self):
        intel = source("plugins/AeroHelmPlatformV2.cs")
        self.assertIn('SortedDictionary<string,double>(StringComparer.Ordinal)', intel)
        self.assertIn('key=key.ToUpperInvariant();', intel)
        self.assertIn('if(parsed.ContainsKey(key))', intel)
        self.assertIn('double.IsNaN(value)||double.IsInfinity(value)', intel)
        self.assertIn('api/v2/vehicles/', intel)
        self.assertNotIn('PARAM_SET', intel)

    def test_native_sitl_and_help_available_without_extra_launcher(self):
        plugin = source("plugins/AeroHelmCloud.cs")
        native = source("scripts/Start-VihangaVedika-GCS.ps1")
        self.assertIn('"AEROHELM_REDUCED_TOOLBAR"), "1"', plugin)
        self.assertIn("'AEROHELM_REDUCED_TOOLBAR'", native)
        self.assertIn('if ($CompactToolbar -and -not $ShowNativeToolbar)', native)
        self.assertNotIn('AEROHELM_TOUCH_STARTUP', native)
        self.assertNotIn('ManualTouch', native)
        self.assertNotIn('Start-AeroHelm-Touch.ps1', native)

    def test_exact_commit_runtime_and_rollback_kept(self):
        updater = source("scripts/Update-AeroHelmLatest.ps1")
        consolidate = source("scripts/Consolidate-AeroHelmWindows.ps1")
        launcher = source("scripts/Start-VihangaVedika-GCS.ps1")
        cleaner = source("scripts/Cleanup-AeroHelmGenerated.ps1")
        branch = "feat/vihanga-vedika-toolbar-ops-intel-phase3"
        for candidate in (updater, consolidate, launcher, cleaner):
            self.assertIn(branch, candidate)
        self.assertIn('merge --ff-only', updater)
        self.assertIn('Copy-Runtime $runtime $snapshot $false', consolidate)
        self.assertIn('Copy-Runtime $snapshot $runtime $true', consolidate)
        self.assertIn('release.executable_sha256', launcher)
        self.assertIn('release.plugin_sha256', launcher)
        self.assertIn('if(-not $Apply)', cleaner)
        self.assertNotIn('git reset --hard', updater)
        self.assertNotIn('git clean -fdx', cleaner)

    def test_legacy_compatible_identifiers_do_not_change_aircraft_authority(self):
        plugin = source("plugins/AeroHelmCloud.cs")
        native = source("MainV2.cs")
        settings = source("plugins/VihangaVedikaPresentation.cs")
        self.assertIn('AeroHelmReceiveOnlyBridge', plugin)
        self.assertIn('AEROHELM_CLOUD_READONLY', native)
        self.assertIn('AeroHelmReleaseIdentity', settings)
        self.assertIn('MissionPlanner.exe', source("scripts/Start-VihangaVedika-GCS.ps1"))
        self.assertNotIn('AeroHelmTouchPanel', settings)


if __name__ == "__main__":
    unittest.main(verbosity=2)
