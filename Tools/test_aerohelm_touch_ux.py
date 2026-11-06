"""Static UI/UX safety contracts for the lightweight native GCS.

Only reads source: no hardware, cloud login, COM port, Mission Planner
execution, or simulated flight is started. Native Windows compilation and
on-screen 100/150/200% DPI checks are separate release gates.
Run: python -m unittest -v Tools.test_aerohelm_touch_ux
"""
from __future__ import annotations

from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


def source(name: str) -> str:
    return (ROOT / name).read_text(encoding="utf-8-sig")


class GcsTouchUxContracts(unittest.TestCase):
    def test_nine_existing_workspaces_remain_touch_accessible(self):
        text = source("plugins/AeroHelmTouchPanel.cs")
        for name in (
            "FLIGHT", "PLAN", "VISION", "OPS", "SECURITY", "FLEET",
            "LINK", "ANALYTICS", "SETTINGS",
        ):
            self.assertIn(f'"{name}"', text)
        self.assertIn("new AeroHelmTileButton(name,Descriptions[i])", text)
        self.assertIn("AccessibleDescription = \"Open \" + name", text)
        self.assertIn("AutoScaleMode = AutoScaleMode.Dpi", text)
        self.assertIn("Drawing.StringTrimming.EllipsisCharacter", text)
        self.assertIn("Height >= 156 && Width >= 180", text)

    def test_keys_and_return_to_native_gcs_cannot_arm_aircraft(self):
        text = source("plugins/AeroHelmTouchPanel.cs")
        self.assertIn("Keys.F1 && e.KeyCode <= Keys.F9", text)
        self.assertIn("e.KeyCode == Keys.F10", text)
        self.assertIn("e.KeyCode == Keys.Escape", text)
        self.assertIn("Close();", text)
        self.assertIn("AeroHelmLinkNavigation.Open(gcs,chosen,heartbeat)", text)
        self.assertIn("main.MenuFlightPlanner.PerformClick()", text)
        self.assertNotIn("doConnect(", text)
        self.assertNotIn("command_long_send", text)

    def test_local_radio_requires_manual_pilot_choice(self):
        text = source("plugins/AeroHelmTouchPanel.cs")
        radio = source("plugins/AeroHelmPilotDirectWindow.cs")
        selector = source("plugins/AeroHelmLinkSelector.cs")
        self.assertIn("PILOT RADIO / MANUAL CHECK REQUIRED", text)
        self.assertIn("pilotRadioButton.Click += (s,e) => Activate(\"PILOT\")", text)
        self.assertIn("new AeroHelmPilotDirectWindow(currentGcs)", text)
        self.assertIn("NO AUTOMATIC FAILOVER", radio)
        self.assertIn("CONNECT SELECTED DIRECT LINK", radio)
        self.assertIn("doConnect(", radio)
        self.assertNotIn("Stop-Process", text)

    def test_modal_workspace_does_not_keep_polling_in_background(self):
        text = source("plugins/AeroHelmTouchPanel.cs")
        self.assertIn("cloudHealthTimer.Interval = 5000", text)
        self.assertIn("bridgeStatusTimer.Interval = 2000", text)
        self.assertIn("bridgeStatusTimer.Stop();", text)
        self.assertIn("cloudHealthTimer.Stop();", text)
        self.assertIn("using (form) form.ShowDialog(this);", text)
        self.assertIn("cloudStatusCheckedUtc = DateTime.MinValue;", text)
        self.assertIn("!Visible", text)
        self.assertIn("WindowState == FormWindowState.Minimized", text)

    def test_cloud_and_radio_are_distinct_statuses_not_auto_handover(self):
        text = source("plugins/AeroHelmTouchPanel.cs")
        selector = source("plugins/AeroHelmLinkSelector.cs")
        self.assertIn("pilotRadioStatus", text)
        self.assertIn("liveBridgeStatus", text)
        self.assertIn("linkStatus", text)
        self.assertIn("CLOUD VIEW / HB + ATTITUDE VERIFIED", text)
        self.assertIn("VIEW ONLY", text)
        self.assertIn("cloudHeartbeatLive", text)
        self.assertIn("pilotRadioStatus.Text = \"PILOT RADIO", text)
        self.assertIn("PILOT BACKUP", selector)
        self.assertIn("AccessibleDescription=details", selector)
        self.assertNotIn("ws.SendAsync(", text)

    def test_fleet_view_uses_server_aggregates_only(self):
        pages = source("plugins/AeroHelmReadOnlyPages.cs")
        self.assertIn('case "cloudMetrics": route = "api/v1/fleet/metrics"', pages)
        self.assertIn('ActionButton(bar,"Cloud metrics", "cloudMetrics")', pages)
        self.assertIn('ActionButton(bar,"Cloud portfolio","cloudMetrics")', pages)
        self.assertIn("CloudV1FleetSnapshot", pages)
        self.assertIn('"api/v1/fleet/status"', pages)
        self.assertIn('"api/v1/vehicles"', pages)
        self.assertIn("CLOUD AGGREGATION / DEVICE-REPORTED", pages)
        self.assertIn("The cloud computes these totals", pages)
        self.assertNotIn('OperationsApi.Request("POST","api/v1/device', pages)
        for banned in ("command_long_send", "param_set_send", "mission_item_send"):
            self.assertNotIn(banned, pages)

    def test_fleet_display_denies_stale_and_simulation_ambiguity(self):
        pages = source("plugins/AeroHelmReadOnlyPages.cs")
        for required in (
            'cloud_flight_authorized', 'physical_control_enabled',
            'Value(telemetry,"session_epoch")', 'age <= 5',
            'fresh ? Value(observation,"mode") : null',
            'fresh ? Value(observation,"armed") : null',
            'fresh ? Value(observation,"battery_pct") : null',
            'TELEMETRY STALE/UNAVAILABLE',
            'SIMULATED / FRESH', 'DEVICE REPORTED',
            'Simulated samples are not physical aircraft evidence.',
        ):
            self.assertIn(required, pages)
        self.assertIn('source.Length > 128', pages)
        self.assertIn('operation != "fleet" && operation != "discover"', pages)

    def test_read_only_page_buttons_and_table_work_at_touch_size(self):
        pages = source("plugins/AeroHelmReadOnlyPages.cs")
        self.assertIn("WrapContents = true, AutoScroll = true", pages)
        self.assertIn("Height = 46", pages)
        self.assertIn("table.RowTemplate.Height = 44", pages)
        self.assertIn("table.ReadOnly = true", pages)
        self.assertIn("MaxJsonLength = 131072", pages)


if __name__ == "__main__":
    unittest.main()
