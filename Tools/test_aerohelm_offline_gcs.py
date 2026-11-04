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
        self.assertIn("TCP 5760 is occupied", session)
        self.assertIn("UseLegacyPythonBridge", session)

    def test_release_never_silently_uses_stale_exe_or_legacy_plugins(self):
        launch = source("scripts/Start-AeroHelm-Touch.ps1")
        release = source("scripts/Consolidate-AeroHelmWindows.ps1")
        for required in (
            "aerohelm-consolidated-release.json",
            "release.executable_sha256",
            "release.plugin_sha256",
            "rev-parse HEAD",
        ):
            self.assertIn(required, launch)
        for required in (
            "Copy-Runtime $runtime $snapshot $false",
            "Copy-Runtime $snapshot $runtime $true",
            "if(-not $Apply)",
            "Get-FileHash",
            "Assert-PluginLayout",
        ):
            self.assertIn(required, release)
        self.assertNotIn("git clean", release)
        self.assertNotIn("git reset", release)

    def test_cloud_and_local_link_banners_are_independent_and_bounded(self):
        home = source("plugins/AeroHelmTouchPanel.cs")
        bridge = source("plugins/AeroHelmReceiveOnlyBridge.cs")
        self.assertIn("bridge.CompactSummary", home)
        self.assertIn("bridgeDiagnosticsTip.SetToolTip", home)
        self.assertIn("bridgeDiagnosticsTip.Dispose();", home)
        self.assertIn("Dock = DockStyle.Bottom, Height = 56", home)
        self.assertIn("CLOUD / FRAMES FRESH, HUD UNVERIFIED", home)
        self.assertIn("LEGACY SERVER", home)
        self.assertIn("PORT 5760 OCCUPIED", bridge)
        self.assertIn("internal string CompactSummary", bridge)
        self.assertNotIn("linkStatus.Text += ", home)
        self.assertNotIn("bridge.Summary + \"  |  READ ONLY\"", home)
        self.assertIn("READ ONLY", home)

    def test_no_native_bridge_launch_with_existing_local_listener(self):
        launcher = source("scripts/Start-AeroHelm-Touch.ps1")
        session = source("scripts/Start-AeroHelm-Session.ps1")
        self.assertIn("if (-not $NoInternalBridge)", launcher)
        self.assertIn("Get-NetTCPConnection -LocalPort 5760 -State Listen", launcher)
        self.assertIn("aerohelm_tcp_bridge[.]py", launcher)
        self.assertIn("Start-AeroHelm-Session.ps1 -UseLegacyPythonBridge", launcher)
        self.assertIn("if ($listener.Count -gt 0 -and -not $SkipBridge)", session)
        self.assertIn("Refusing to reuse it.", session)
        self.assertNotIn("Stop-Process", launcher)

    def test_real_attitude_preview_is_authenticated_and_never_inferred(self):
        link = source("plugins/AeroHelmWorkspace.cs")
        self.assertIn('OperationsApi.Request("GET","api/v1/mavlink/attitude")', link)
        self.assertIn("AeroHelmCloudOrigin", link)
        self.assertIn("Observed ATTITUDE: ", link)
        self.assertIn("STALE/MISSING — NO LIVE ATTITUDE", link)
        self.assertIn("HUD HEARTBEAT NOT VERIFIED", link)
        self.assertIn("GCS viewers: ", link)
        self.assertIn("none attached; click FLIGHT", link)

    def test_research_and_kerberos_are_not_misrepresented_as_deployed(self):
        pages = source("plugins/AeroHelmReadOnlyPages.cs")
        self.assertIn('ActionButton(bar,"Cloud capabilities", "capabilities")', pages)
        self.assertIn('case "capabilities": route = "api/v3/research/capabilities";', pages)
        self.assertIn("Research V3 is not deployed or enabled", pages)
        self.assertIn("Kerberos integration: NOT PROVISIONED", pages)
        self.assertIn("requires a provisioned KDC, SPNEGO gateway", pages)

    def test_explicit_verified_bridge_stop_is_not_an_unguarded_process_kill(self):
        session = source("scripts/Start-AeroHelm-Session.ps1")
        self.assertIn("[switch]$StopVerifiedLegacyBridge", session)
        self.assertIn("if ($StopVerifiedLegacyBridge", session)
        self.assertIn("Get-CimInstance Win32_Process", session)
        self.assertIn("aerohelm_tcp_bridge[.]py", session)
        self.assertIn("Stop-Process -Id $ownerId -ErrorAction Stop", session)
        self.assertIn("Refusing to reuse it.", session)

    def test_six_dof_physics_is_research_only_and_visible_from_analytics(self):
        twin = source("plugins/AeroHelmTwinLabWindow.cs")
        pages = source("plugins/AeroHelmReadOnlyPages.cs")
        fixture = source("Tools/fixtures/physics_lab_hover.json")
        self.assertIn("internal sealed class AeroHelmPhysicsLabWindow", twin)
        self.assertIn("api/v3/research/physics-lab/evaluate", twin)
        self.assertIn("MessageBoxButtons.YesNo", twin)
        self.assertIn("No aircraft command occurred.", twin)
        self.assertIn('Text="Six-DOF physics lab"', pages)
        self.assertIn("new AeroHelmPhysicsLabWindow()", pages)
        self.assertIn("NOT measured from an actual airframe", fixture)

    def test_latest_release_update_never_resets_source_or_hijacks_ports(self):
        update = source("scripts/Update-AeroHelmLatest.ps1")
        self.assertIn("git -C $root merge --ff-only", update)
        self.assertIn("git -C $root status --porcelain", update)
        self.assertIn("Repair-AeroHelmWindows.ps1", update)
        self.assertIn("Consolidate-AeroHelmWindows.ps1", update)
        self.assertIn("StopVerifiedLegacyBridge", update)
        self.assertNotIn("git reset --hard", update)
        self.assertNotIn("git clean -fdx", update)
        self.assertNotIn("Stop-Process", update)

    def test_login_modal_cannot_be_hidden_by_topmost_splash(self):
        program = source("Program.cs")
        gate = source("AeroHelmStartupGate.cs")
        splash = source("Splash.Designer.cs")
        self.assertIn("this.TopMost = true;", splash)
        login_start = program.index("if (AeroHelmStartupGate.ShouldRequire(Program.args))")
        login_end = program.index("Application.Run(new MainV2());", login_start)
        sequence = program[login_start:login_end]
        self.assertLess(sequence.index("Splash.Hide()"),
                        sequence.index("AeroHelmStartupGate.Authenticate()"))
        self.assertLess(sequence.index("AeroHelmStartupGate.Authenticate()"),
                        sequence.index("Splash.Show()"))
        self.assertIn("Splash.TopMost = false;", sequence)
        self.assertIn("password.Focus();", gate)
        self.assertIn("ShowDialog()==DialogResult.OK", gate)

    def test_wip_windows_build_is_not_implicitly_deployed(self):
        build = source("scripts/Build-Integrated-AeroHelm.ps1")
        self.assertIn("AeroHelmMavlinkBridgeTests.csproj", build)
        self.assertIn("Test-AeroHelmWindowsPages.ps1", build)
        self.assertIn("Install-AeroHelmCloud.ps1", build)
        self.assertIn("Close all AeroHelm/MissionPlanner windows", build)


if __name__ == "__main__":
    unittest.main(verbosity=2)
