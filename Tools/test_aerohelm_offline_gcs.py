"""AeroHelm no-flight-controller GCS regression checks.

Run from the GCS repository root:
    python -m unittest -v Tools.test_aerohelm_offline_gcs

These checks validate source wiring. They DO NOT substitute for Windows
MSBuild compilation, live authentication or propeller-off Pixhawk acceptance.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


class OfflineGcsContracts(unittest.TestCase):
    def test_readonly_tcp_always_uses_local_bridge(self):
        native = source("MainV2.cs")
        self.assertIn('bool aeroHelmReadOnlyTcp =', native)
        self.assertIn('string.Equals(portname, "TCP", StringComparison.OrdinalIgnoreCase)', native)
        self.assertIn('"AEROHELM_CLOUD_READONLY"', native)
        self.assertRegex(native, re.compile(
            r"bool aeroHelmReadOnlyTcp =.*?if \(aeroHelmReadOnlyTcp\).*?getparams = false;",
            re.DOTALL))
        self.assertRegex(native, re.compile(
            r'case "TCP":.*?if \(aeroHelmReadOnlyTcp\).*?Host = "127\.0\.0\.1", '
            r'Port = "5760"', re.DOTALL))

    def test_gcs_cloud_commands_cannot_escape_native_receive_only_adapter(self):
        native = source("plugins/AeroHelmReceiveOnlyBridge.cs")
        self.assertIn("IPAddress.Loopback, 5760", native)
        self.assertIn("DrainGcsAsync", native)
        self.assertIn("ClientWebSocket", native)
        self.assertIn('ws.Options.SetRequestHeader("Authorization"', native)
        self.assertIn("TryReadAttitude", native)
        self.assertIn("lastHeartbeatUtcTicks", native)
        self.assertIn("lastAttitudeUtcTicks", native)
        self.assertIn("roll/pitch STALE OR UNKNOWN", native)
        for forbidden in ("ws.SendAsync(", "ws.Send(", "command_long_send",
                          "param_set_send", "mission_item_send"):
            self.assertNotIn(forbidden, native)

    def test_hud_uses_real_flight_data_and_fails_closed_when_stale(self):
        home = source("plugins/AeroHelmTouchPanel.cs")
        self.assertIn('main.doConnect(MainV2.comPort,"TCP","57600"', home)
        self.assertIn("RefreshCloudStatusAsync", home)
        self.assertIn("native_hud_heartbeat_ready", home)
        self.assertIn("attitude_stream_active", home)
        self.assertIn("cloudHealthTimer.Interval = 5000", home)
        self.assertIn("CLOUD / STALE OR DISCONNECTED", home)

    def test_module_icons_and_reduced_operator_toolbar(self):
        toolbar = source("plugins/AeroHelmCloud.cs")
        art = source("plugins/AeroHelmTouchPanel.cs")
        self.assertIn("AeroHelmIconPainter.Make(label,20)", toolbar)
        for icon in ("CLOUD", "SURVEY", "VISION", "LINK",
                     "OPS", "INTEL", "TOUCH"):
            if icon in ("CLOUD", "VISION"):
                self.assertIn(f'AeroHelmIconPainter.Make("{icon}"', toolbar)
            else:
                self.assertIn(f'NewToolbarButton("{icon}"', toolbar)
        for icon in ("CLOUD", "SURVEY", "INTEL", "TOUCH"):
            self.assertIn(f'case "{icon}":', art)
        self.assertIn("main.MenuSimulation.Visible = false", toolbar)
        self.assertIn("main.MenuHelp.Visible = false", toolbar)

    def test_no_second_python_bridge_when_native_receiver_enabled(self):
        launch = source("scripts/Start-AeroHelm-Touch.ps1")
        session = source("scripts/Start-AeroHelm-Session.ps1")
        self.assertIn("AEROHELM_CLOUD_READONLY", launch)
        self.assertIn("AEROHELM_INTERNAL_TCP_BRIDGE", launch)
        self.assertIn("AEROHELM_AUTO_NATIVE_TCP", launch)
        self.assertIn("TCP 5760 is in use", session)
        self.assertIn("UseLegacyPythonBridge", session)

    def test_wip_windows_build_is_not_implicitly_deployed(self):
        build = source("scripts/Build-Integrated-AeroHelm.ps1")
        self.assertIn("AeroHelmMavlinkBridgeTests.csproj", build)
        self.assertIn("Test-AeroHelmWindowsPages.ps1", build)
        self.assertIn("Install-AeroHelmCloud.ps1", build)
        self.assertIn("Close all AeroHelm/MissionPlanner windows", build)


if __name__ == "__main__":
    unittest.main(verbosity=2)
