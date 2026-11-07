// VIHANGA Cloud — isolated Mission Planner plugin (first integration milestone).
// No MAVLink transport is installed or replaced by this plugin.
// Cloud "Connect" is an operator telemetry VIEW session, never flight authority.
// Requires HTTPS except loopback development. Credentials/tokens stay in memory.
// Mission Planner retains its original USB/TCP/UDP connection functionality.
//loadassembly: System.Web.Extensions
// Copyright (C) AeroHelm contributors. GPL-3.0-or-later (see repository COPYING.txt).

extern alias FrameworkDrawing;

using MissionPlanner;
using MissionPlanner.Plugin;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    public sealed class AeroHelmCloudPlugin : MissionPlanner.Plugin.Plugin
    {
        private ToolStripButton _vision;
        private ToolStripButton _paths;
        private ToolStripDropDownButton _workspaces;
        internal static AeroHelmReceiveOnlyBridge ActiveNativeBridge;
        private bool _simulationWasVisible;
        private bool _helpWasVisible;
        private readonly System.Windows.Forms.Timer _cloudLinkWatchdog =
            new System.Windows.Forms.Timer();
        private bool _cloudProbeBusy;

        public override string Name { get { return "Vihanga Ground Station"; } }
        public override string Version { get { return "0.3.0-rc1"; } }
        public override string Author { get { return "AeroHelm Contributors"; } }

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            // Mission Planner uses an icon-oriented MenuStrip with the COM selectors
            // and ArduPilot logo aligned to the right. Appending a ToolStripMenuItem
            // after those items can leave it hidden in the overflow area.
            // Place a distinct, text-only button immediately after HELP instead.
            var main = MainV2.instance;
            var menu = main.MainMenu;
            // Keep the native DATA / PLAN / SETUP / CONFIG entries.
            // Three Vihanga entries only: link selection, camera, utilities.
            // No duplicate CLOUD registry login or separate STATUS and HUD tiles.
            int configIndex = menu.Items.IndexOf(main.MenuConfigTune);
            int at = configIndex >= 0 ? configIndex + 1 :
                Math.Max(0, menu.Items.IndexOf(main.MenuHelp));

            _paths = NewToolbarButton("PATHS",
                "Select cloud telemetry or an independent USB/radio link");
            _paths.Click += delegate
            {
                using (var picker = new AeroHelmLinkSelector())
                {
                    if (picker.ShowDialog(main) == DialogResult.OK)
                        AeroHelmLinkNavigation.Open(main, picker.Selection, null);
                }
            };
            menu.Items.Insert(at++, _paths);

            _vision = NewToolbarButton("CAMERA", "Open onboard camera viewer");
            _vision.Click += delegate
            {
                using (var window = new AeroHelmVisionWindow())
                    window.ShowDialog(main);
            };
            menu.Items.Insert(at++, _vision);

            _workspaces = new ToolStripDropDownButton("TOOLS")
            {
                Name = "VihangaToolsButton",
                ToolTipText = "Diagnostics, survey, simulated operations and settings",
                Image = AeroHelmIconPainter.Make("SETTINGS",20),
                ImageScaling = ToolStripItemImageScaling.None,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ForeColor = FrameworkDrawing::System.Drawing.Color.White,
                Margin = new Padding(4,0,4,0),
                AutoSize = true
            };
            _workspaces.DropDownItems.Add("Link diagnostics", null,
                (sender,e) => ShowWorkspace(new AeroHelmLinkWindow()));
            _workspaces.DropDownItems.Add("Attitude monitor", null,
                (sender,e) =>
                {
                    if (VihangaLinkLoginWindow.EnsureSignedIn(MainV2.instance))
                        ShowWorkspace(new AeroHelmCloudAttitudeWindow());
                });
            _workspaces.DropDownItems.Add("Survey planner", null,
                (sender,e) => ShowWorkspace(new AeroHelmSurveyWindow()));
            _workspaces.DropDownItems.Add("Operations / simulation", null,
                (sender,e) => ShowWorkspace(new AeroHelmOpsWindow()));
            var planPreview = new ToolStripMenuItem("Autonomy preview");
            planPreview.Visible = VihangaLinkSession.UsesDedicatedRelay;
            planPreview.Click += (sender,e) =>
            {
                if (VihangaLinkLoginWindow.EnsureSignedIn(MainV2.instance))
                    ShowWorkspace(new VihangaMissionPreviewWindow());
            };
            _workspaces.DropDownItems.Add(planPreview);
            _workspaces.DropDownItems.Add("Fleet and intelligence", null,
                (sender,e) => ShowWorkspace(new AeroHelmIntelWindow()));
            _workspaces.DropDownItems.Add(new ToolStripSeparator());
            _workspaces.DropDownItems.Add("Vihanga settings", null,
                (sender,e) => ShowWorkspace(new VihangaSettingsWindow()));
            menu.Items.Insert(at++, _workspaces);
            // This monitor stays active while the native Flight Data workspace is in use and the
            // pilot opens native Flight Data. It NEVER connects to a radio,
            // sends MAVLink or initiates failover. Its only output is a
            // persistent, nonmodal LINK/PATHS warning to the operator.
            _cloudLinkWatchdog.Interval=5000;
            _cloudLinkWatchdog.Tick+=async(sender,args)=>await CheckCloudLinkAsync();
            _cloudLinkWatchdog.Start();
            _ = CheckCloudLinkAsync();
            _simulationWasVisible = main.MenuSimulation.Visible;
            _helpWasVisible = main.MenuHelp.Visible;
            if (!string.Equals(Environment.GetEnvironmentVariable(
                    "AEROHELM_REDUCED_TOOLBAR"), "0", StringComparison.Ordinal))
            {
                main.MenuSimulation.Visible = false;
                main.MenuHelp.Visible = false;
            }

            // AeroHelm operational profile: simplify the toolbar only.
            // Do not delete Simulation or Help functionality, which upstream
            // may still need for diagnosis, SITL and maintenance.
            _simulationWasVisible = main.MenuSimulation.Visible;
            _helpWasVisible = main.MenuHelp.Visible;
            // AeroHelm GCS hides the upstream HELP and SIMULATION tiles in its
            // default operator profile, as requested. They remain present in
            // the native offline maintenance/recovery executable, not deleted.
            // Only an explicit maintenance override may restore their buttons.
            if (!string.Equals(Environment.GetEnvironmentVariable(
                    "AEROHELM_REDUCED_TOOLBAR"), "0", StringComparison.Ordinal))
            {
                main.MenuSimulation.Visible = false;
                main.MenuHelp.Visible = false;
            }
            // The native loopback bridge consumes the already authenticated
            // in-memory startup session: one cloud login, no Python terminal.
            // Never run both the native and legacy Python bridges on port 5760.
            if (string.Equals(Environment.GetEnvironmentVariable(
                    "AEROHELM_INTERNAL_TCP_BRIDGE"), "1", StringComparison.Ordinal))
            {
                ActiveNativeBridge = new AeroHelmReceiveOnlyBridge();
                ActiveNativeBridge.Start(); // Fail closed if login/port unavailable.
            }

            // Mission Planner remains the operator's primary workspace.
            return true;
        }

        private async Task CheckCloudLinkAsync()
        {
            if (_cloudProbeBusy || _paths == null || _paths.IsDisposed) return;
            _cloudProbeBusy = true;
            try
            {
                // A dedicated private telemetry relay owns its own token and
                // receiver. Never infer its HUD health from Oracle M1 status.
                if (VihangaLinkSession.UsesDedicatedRelay)
                {
                    var active = ActiveNativeBridge;
                    bool hbLive = active != null && active.NativeHeartbeatFresh;
                    bool attLive = active != null && active.NativeAttitudeFresh;
                    _paths.Text = "PATHS";
                    _paths.ForeColor = hbLive && attLive ?
                        FrameworkDrawing::System.Drawing.Color.FromArgb(195,228,195) :
                        FrameworkDrawing::System.Drawing.Color.FromArgb(242,198,139);
                    _paths.ToolTipText = hbLive && attLive ?
                        "Private relay: live Pixhawk heartbeat and attitude." :
                        hbLive ? "Private relay: heartbeat live; attitude missing." :
                        "Private relay: not receiving verified native MAVLink.";
                    return;
                }
                // Local USB/telemetry-radio recovery is usable without
                // Oracle, cloud credentials or Kerberos. Never treat the
                // absence of a cloud token as a native aircraft fault.
                if(string.IsNullOrWhiteSpace(Program.AeroHelmOperatorToken) ||
                   DateTime.UtcNow >= Program.AeroHelmTokenExpiresUtc)
                {
                    _paths.Text = "PATHS";
                    _paths.ToolTipText = "CLOUD NOT AUTHENTICATED — pilot direct COM/radio remains independent";
                    _paths.ForeColor = FrameworkDrawing::System.Drawing.Color.FromArgb(
                        242,198,139);
                    return;
                }
                string raw = await Task.Run(()=>OperationsApi.Request(
                    "GET","api/v1/mavlink/health"));
                if(_paths == null || _paths.IsDisposed) return;
                var result = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string,object>>(raw);
                object online, fresh, hb, att;
                bool link = result.TryGetValue("device_online",out online) &&
                            online is bool && (bool)online &&
                            result.TryGetValue("fresh",out fresh) &&
                            fresh is bool && (bool)fresh;
                bool hbKnown = result.TryGetValue("native_hud_heartbeat_ready",out hb)
                               && hb is bool;
                bool attKnown = result.TryGetValue("attitude_stream_active",out att)
                                && att is bool;
                bool ready = link && hbKnown && (bool)hb &&
                             attKnown && (bool)att;
                _paths.Text = "PATHS";
                _paths.ForeColor = ready ?
                    FrameworkDrawing::System.Drawing.Color.FromArgb(195,228,195) :
                    FrameworkDrawing::System.Drawing.Color.FromArgb(242,198,139);
                _paths.ToolTipText = ready ?
                    "Cloud: heartbeat and attitude verified; choose link." :
                    !link ?
                    "Cloud: offline or stale; direct link requires pilot selection." :
                    !hbKnown || !attKnown ?
                    "Cloud: frames received; heartbeat and attitude not verified." :
                    "Cloud: heartbeat or attitude missing.";
            }
            catch (Exception)
            {
                if(_paths != null && !_paths.IsDisposed)
                {
                    _paths.Text="PATHS";
                    _paths.ForeColor=FrameworkDrawing::System.Drawing.Color.FromArgb(
                        244,177,150);
                    _paths.ToolTipText="Cloud status unavailable; direct connection remains independent.";
                }
            }
            finally { _cloudProbeBusy = false; }
        }

        private static void ShowWorkspace(Form window)
        {
            using (window)
                window.ShowDialog(MainV2.instance);
        }

        private static ToolStripButton NewToolbarButton(string label, string tip)
        {
            return new ToolStripButton(label)
            {
                Name = "AeroHelm" + label + "Button",
                ToolTipText = tip,
                Image = AeroHelmIconPainter.Make(label,20),
                ImageScaling = ToolStripItemImageScaling.None,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ForeColor = FrameworkDrawing::System.Drawing.Color.White,
                Margin = new Padding(4, 0, 4, 0),
                AutoSize = true
            };
        }

        private static FrameworkDrawing::System.Drawing.Bitmap CameraIcon()
        {
            // Render vector camera artwork at runtime: no third-party icons/fonts.
            var img = new FrameworkDrawing::System.Drawing.Bitmap(20, 20);
            using (var g = FrameworkDrawing::System.Drawing.Graphics.FromImage(img))
            using (var pen = new FrameworkDrawing::System.Drawing.Pen(
                FrameworkDrawing::System.Drawing.Color.White, 1.6f))
            {
                g.SmoothingMode = FrameworkDrawing::System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawRectangle(pen, 2, 6, 16, 11);
                g.DrawEllipse(pen, 7, 9, 6, 6);
                g.DrawLine(pen, 5, 6, 7, 3);
                g.DrawLine(pen, 7, 3, 12, 3);
                g.DrawLine(pen, 12, 3, 14, 6);
            }
            return img;
        }

        public override bool Exit()
        {
            _cloudLinkWatchdog.Stop();
            _cloudLinkWatchdog.Dispose();
            if (ActiveNativeBridge != null)
            {
                ActiveNativeBridge.Dispose();
                ActiveNativeBridge = null;
            }
            var main = MainV2.instance;
            if (main != null && !main.IsDisposed)
            {
                main.MenuSimulation.Visible = _simulationWasVisible;
                main.MenuHelp.Visible = _helpWasVisible;
                foreach (var item in new ToolStripItem[] { _paths, _vision, _workspaces })
                    if (item != null) main.MainMenu.Items.Remove(item);
            }
            foreach (ToolStripItem item in new ToolStripItem[] { _paths, _vision, _workspaces })
            {
                if (item == null) continue;
                if (item.Image != null)
                {
                    var owned = item.Image;
                    item.Image = null;
                    owned.Dispose();
                }
                item.Dispose();
            }
            _paths = null;
            _vision = null;
            _workspaces = null;
            return true;
        }
    }

}
