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
    def test_native_mavlink_harness_qualifies_mission_planner_login_stub(self):
        harness = source("Tools/AeroHelmMavlinkBridgeTests/Program.cs")
        self.assertIn("namespace MissionPlanner", harness)
        self.assertIn("public static class Program", harness)
        for field in ("AeroHelmOperatorToken", "AeroHelmCloudOrigin",
                      "AeroHelmTokenExpiresUtc"):
            self.assertIn(f"global::MissionPlanner.Program.{field}", harness)
        self.assertNotRegex(
            harness,
            r"(?<![A-Za-z0-9_.:])Program[.]AeroHelm(?:OperatorToken|CloudOrigin|TokenExpiresUtc)",
        )

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
        self.assertIn("HasValidTelemetryChecksum", native)
        self.assertIn("cloudStreamOpen && metrics.HeartbeatFresh", native)
        self.assertIn("cloudStreamOpen && metrics.AttitudeFresh", native)
        self.assertIn("quality.Reset()", native)
        self.assertIn("quality.RejectEvidence()", native)
        quality = source("plugins/AeroHelmLinkQuality.cs")
        self.assertIn("Stopwatch.GetTimestamp()", quality)
        self.assertIn("internal void ObserveAt(", quality)
        self.assertIn("internal AeroHelmQualitySnapshot SnapshotAt(", quality)
        self.assertIn("lastTick = lastHeartbeat = lastAttitude = Missing", quality)
        plugin = source("Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj")
        harness = source("Tools/AeroHelmMavlinkBridgeTests/AeroHelmMavlinkBridgeTests.csproj")
        self.assertIn("AeroHelmLinkQuality.cs", plugin)
        self.assertIn("AeroHelmLinkQuality.cs", harness)
        self.assertIn("roll/pitch STALE OR UNKNOWN", native)
        for forbidden in ("ws.SendAsync(", "ws.Send(", "command_long_send",
                          "param_set_send", "mission_item_send"):
            self.assertNotIn(forbidden, native)

    def test_duplex_link_is_authenticated_and_never_cloud_flight_control(self):
        link = source("plugins/AeroHelmWorkspace.cs")
        self.assertIn('ProbeDuplexAsync("ping")', link)
        self.assertIn('ProbeDuplexAsync("uplink_status")', link)
        self.assertIn('"api/v1/network/requests"', link)
        self.assertIn('"api/v1/network/requests/" + id', link)
        self.assertIn('RequireAuthenticatedOrigin()', link)
        self.assertIn('StringComparison.OrdinalIgnoreCase', link)
        self.assertIn('report["rtt_ms"]', link)
        self.assertIn('Authenticated diagnostics only. Flight commands remain blocked.', link)
        self.assertNotIn('ws.SendAsync(', link)

    def test_hud_uses_real_flight_data_and_fails_closed_when_stale(self):
        selector = source("plugins/AeroHelmLinkSelector.cs")
        cloud = source("plugins/AeroHelmCloud.cs")
        self.assertIn('main.doConnect(MainV2.comPort,"TCP","57600"', selector)
        self.assertIn('AeroHelmLinkPath.PilotDirect', selector)
        self.assertIn('bridge.Start()', selector)
        self.assertIn('NO AUTO-FAILOVER', selector)
        self.assertIn('if (!bridge.IsListening && !bridge.Start())', selector)
        self.assertIn('MainV2.comPort.BaseStream.IsOpen', selector)
        self.assertIn('_cloudLinkWatchdog.Interval=5000', cloud)
        self.assertIn('native_hud_heartbeat_ready', cloud)
        self.assertIn('attitude_stream_active', cloud)
        self.assertIn('CLOUD HEARTBEAT/ATTITUDE MISSING', cloud)
        self.assertIn('NO automatic failover', cloud)

    def test_module_icons_and_reduced_operator_toolbar(self):
        toolbar = source("plugins/AeroHelmCloud.cs")
        icons = source("plugins/VihangaSharedUi.cs")
        self.assertIn("AeroHelmIconPainter.Make(label,20)", toolbar)
        for icon in ("CLOUD", "SURVEY", "VISION", "LINK", "OPS",
                     "INTEL", "PATHS"):
            self.assertIn(f'case "{icon}":', icons)
        self.assertIn('new ToolStripDropDownButton("MORE")', toolbar)
        for module in ("FLEET", "SECURITY", "ANALYTICS"):
            self.assertIn(f'new AeroHelmReadOnlyPage("{module}")', toolbar)
        self.assertIn('new VihangaSettingsWindow()', toolbar)
        self.assertNotIn("AeroHelmTouchPanel", toolbar)
        self.assertIn("main.MenuSimulation.Visible = false", toolbar)
        self.assertIn("main.MenuHelp.Visible = false", toolbar)

    def test_no_second_python_bridge_when_native_receiver_enabled(self):
        launch = source("scripts/Start-VihangaGCS.ps1")
        session = source("scripts/Start-AeroHelm-Session.ps1")
        self.assertIn("AEROHELM_CLOUD_READONLY", launch)
        self.assertIn("AEROHELM_INTERNAL_TCP_BRIDGE", launch)
        self.assertIn("AEROHELM_AUTO_NATIVE_TCP", launch)
        self.assertIn("TCP 5760 is occupied", session)
        self.assertIn("UseLegacyPythonBridge", session)

    def test_release_never_silently_uses_stale_exe_or_legacy_plugins(self):
        launch = source("scripts/Start-VihangaGCS.ps1")
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
        cloud = source("plugins/AeroHelmCloud.cs")
        bridge = source("plugins/AeroHelmReceiveOnlyBridge.cs")
        selector = source("plugins/AeroHelmLinkSelector.cs")
        self.assertIn("_paths.ToolTipText", cloud)
        self.assertIn("CLOUD STALE/OFFLINE", cloud)
        self.assertIn("PILOT DIRECT is independent", cloud)
        self.assertIn("PORT 5760 OCCUPIED", bridge)
        self.assertIn("internal string CompactSummary", bridge)
        self.assertIn("NO AUTO-FAILOVER", selector)

    def test_no_native_bridge_launch_with_existing_local_listener(self):
        launcher = source("scripts/Start-VihangaGCS.ps1")
        session = source("scripts/Start-AeroHelm-Session.ps1")
        self.assertIn("if (-not $NoInternalBridge)", launcher)
        self.assertIn("Get-NetTCPConnection -LocalPort 5760 -State Listen", launcher)
        self.assertIn("aerohelm_tcp_bridge[.]py", launcher)
        self.assertIn("Start-AeroHelm-Session.ps1 -UseLegacyPythonBridge", launcher)
        self.assertIn("if ($listeners.Count -gt 0 -and -not $SkipBridge)", session)
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
        self.assertIn('ActionButton(bar,"Kerberos readiness", "kerberos")', pages)
        self.assertIn('if (operation == "kerberos")', pages)
        self.assertIn('table.Rows.Add("Kerberos KDC/realm", "NOT PROVISIONED")', pages)
        self.assertIn("no token or ticket created", pages)
        self.assertIn("requires a provisioned KDC, SPNEGO gateway", pages)

    def test_explicit_cloud_and_pilot_radio_backup_paths(self):
        selector = source("plugins/AeroHelmLinkSelector.cs")
        toolbar = source("plugins/AeroHelmCloud.cs")
        csproj = source("Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj")
        self.assertIn("AeroHelmLinkPath { Cancel, CloudReadOnly, PilotDirect, CloudAttitudePreview }", selector)
        self.assertIn("NO AUTO-FAILOVER", selector)
        self.assertIn("normal COM port and baud controls", selector)
        self.assertIn("if (!bridge.IsListening && !bridge.Start())", selector)
        self.assertIn("main.MenuFlightData.PerformClick();", selector)
        self.assertIn("main.doConnect(MainV2.comPort", selector)
        self.assertIn('NewToolbarButton("PATHS"', toolbar)
        self.assertIn("AeroHelmLinkNavigation.Open(MainV2.instance,", toolbar)
        self.assertIn("AeroHelmLinkSelector.cs", csproj)
        self.assertNotIn("Stop-Process", selector)
        self.assertNotIn("command_long_send", selector)

    def test_session_launcher_has_one_unbroken_port_and_login_flow(self):
        session = source("scripts/Start-AeroHelm-Session.ps1")
        self.assertEqual(session.count("AEROHELM_VISION_AUTO_OPEN"), 1)
        self.assertEqual(session.count("AEROHELM_CLOUD_URL"), 1)
        self.assertEqual(session.count("$launchArgs = @("), 1)
        self.assertEqual(session.count("Start-Process -FilePath $python.Source"), 1)
        self.assertEqual(session.count("Stop-Process -Id $listenerPid"), 1)
        self.assertEqual(session.count("Get-NetTCPConnection -LocalPort 5760"), 2)
        self.assertIn("$expectedScriptPath = [regex]::Escape($legacyFile)", session)
        self.assertIn("The owner of TCP 5760 changed", session)
        checks = source("scripts/Test-AeroHelmWindowsPages.ps1")
        self.assertIn("[System.Management.Automation.Language.Parser]::ParseFile(", checks)
        self.assertIn("PowerShell SYNTAX FAILED", checks)

    def test_explicit_verified_bridge_stop_is_not_an_unguarded_process_kill(self):
        session = source("scripts/Start-AeroHelm-Session.ps1")
        self.assertIn("[switch]$StopVerifiedLegacyBridge", session)
        self.assertIn("if ($StopVerifiedLegacyBridge", session)
        self.assertIn("Get-CimInstance Win32_Process", session)
        self.assertIn("scripts\\aerohelm_tcp_bridge.py", session)
        self.assertIn("Stop-Process -Id $listenerPid -ErrorAction Stop", session)
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

    def test_paths_cloud_retry_and_local_radio_are_separate(self):
        selector=source("plugins/AeroHelmLinkSelector.cs")
        toolbar=source("plugins/AeroHelmCloud.cs")
        project=source("Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj")
        self.assertIn('AeroHelmLinkPath.PilotDirect',selector)
        self.assertIn('bridge.Start()',selector)
        self.assertIn('main.doConnect(MainV2.comPort,"TCP","57600"',selector)
        self.assertIn('OperationsApi.Request("GET","api/v1/mavlink/paths")',selector)
        self.assertIn('NO AUTO-FAILOVER',selector)
        self.assertIn('new AeroHelmLinkSelector()',toolbar)
        self.assertIn('NewToolbarButton("PATHS"',toolbar)
        self.assertIn('AeroHelmLinkSelector.cs',project)
        self.assertIn("AeroHelm will not change control authority automatically.", selector)
        self.assertIn("MainV2.comPort.BaseStream.IsOpen",selector)
        self.assertIn("Disconnect it ",selector)
        self.assertIn("main.doConnect(MainV2.comPort",selector)

    def test_observed_cloud_attitude_and_offline_direct_backup(self):
        cloud = source("plugins/AeroHelmCloudAttitudeWindow.cs")
        paths = source("plugins/AeroHelmLinkSelector.cs")
        project = source("Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj")
        backup = source("scripts/Start-AeroHelm-DirectBackup.ps1")
        touch = source("scripts/Start-VihangaGCS.ps1")
        self.assertIn("api/v1/mavlink/attitude", cloud)
        self.assertIn("canvas.Stale()", cloud)
        self.assertIn("float.IsNaN(roll)", cloud)
        self.assertIn("CloudAttitudePreview", paths)
        self.assertIn("AeroHelmCloudAttitudeWindow.cs", project)
        self.assertIn("AcknowledgePilotAuthority", backup)
        self.assertIn("-NoInternalBridge", backup)
        self.assertIn("-NoAutoTcp", backup)
        self.assertIn("--local-maintenance", touch)

    def test_enterprise_sso_is_opt_in_and_pinned_to_exact_origin(self):
        gate=source("AeroHelmStartupGate.cs")
        self.assertIn('"AEROHELM_KERBEROS_UI_ENABLED"',gate)
        self.assertIn('"AEROHELM_KERBEROS_SSO_ORIGIN"',gate)
        self.assertIn('cache.Add(url,"Negotiate"',gate)
        self.assertIn('req.AllowAutoRedirect=false;',gate)
        self.assertIn('kerberos.Visible=',gate)
        self.assertIn('Kerberos SSO unavailable.',gate)
        self.assertIn('private async Task Login()',gate)
        self.assertNotIn('cache.Add(url,"Basic"',gate)

    def test_direct_pilot_module_is_compiled_and_never_auto_fails_over(self):
        direct = source("plugins/AeroHelmPilotDirectWindow.cs")
        selector = source("plugins/AeroHelmLinkSelector.cs")
        project = source("Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj")
        self.assertIn("AeroHelmPilotDirectWindow.cs", project)
        self.assertIn("new AeroHelmPilotDirectWindow(main)", selector)
        self.assertIn("NO AUTO-FAILOVER", selector)
        self.assertIn("SerialPort.GetPortNames()", direct)
        self.assertIn("MessageBoxButtons.YesNo", direct)
        self.assertIn("MessageBoxDefaultButton.Button2", direct)
        self.assertIn("main.doConnect(MainV2.comPort,port,baud,true,true)", direct)
        self.assertIn("MainV2.comPort.BaseStream.IsOpen", direct)
        self.assertNotIn("Stop-Process", direct)
        self.assertNotIn("ws.SendAsync(", direct)
        self.assertNotIn("autoReconnect = true", direct)

    def test_kerberos_sso_uses_exact_cloud_origin(self):
        gate = source("AeroHelmStartupGate.cs")
        self.assertIn("string.Equals(ssoOrigin,address,StringComparison.OrdinalIgnoreCase)", gate)
        self.assertIn("Kerberos gateway must share the exact cloud HTTPS origin.", gate)

    def test_persistent_cloud_watchdog_never_auto_switches_pilot_link(self):
        toolbar = source("plugins/AeroHelmCloud.cs")
        self.assertIn("_cloudLinkWatchdog.Interval=5000", toolbar)
        self.assertIn("async Task CheckCloudLinkAsync()", toolbar)
        self.assertIn('OperationsApi.Request(', toolbar)
        self.assertIn('"GET","api/v1/mavlink/health"', toolbar)
        self.assertIn("NO automatic failover", toolbar)
        self.assertIn("PILOT DIRECT is independent", toolbar)
        self.assertIn("_cloudLinkWatchdog.Dispose()", toolbar)
        method = toolbar.split("private async Task CheckCloudLinkAsync()",1)[1]
        method = method.split("private static ToolStripButton NewToolbarButton",1)[0]
        for forbidden in ("main.doConnect", "Stop-Process", "ws.SendAsync",
                          "MAV_CMD", "PARAM_SET"):
            self.assertNotIn(forbidden, method)

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
