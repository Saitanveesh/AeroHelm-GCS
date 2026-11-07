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
using System.IO;
using System.Net;
using System.Text;
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
            _workspaces.DropDownItems.Add("Survey planner", null,
                (sender,e) => ShowWorkspace(new AeroHelmSurveyWindow()));
            _workspaces.DropDownItems.Add("Operations / simulation", null,
                (sender,e) => ShowWorkspace(new AeroHelmOpsWindow()));
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

    internal sealed class CloudWindow : Form
    {
        private readonly TextBox _server = new TextBox();
        private readonly TextBox _username = new TextBox();
        private readonly TextBox _password = new TextBox();
        private readonly Button _login = new Button();
        private readonly Button _refresh = new Button();
        private readonly Button _connect = new Button();
        private readonly Button _disconnect = new Button();
        private readonly ListBox _aircraft = new ListBox();
        private readonly Label _status = new Label();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private string _baseUrl;
        private string _accessToken;
        private DateTime _tokenDeadline = DateTime.MinValue;
        private string _connectionId;
        private List<AircraftInfo> _aircraftData = new List<AircraftInfo>();
        private bool _busy;

        // Neutral, high-contrast interface; deliberately separate from legacy
        // Mission Planner button theming. Hardware actions remain unavailable.
        private static readonly FrameworkDrawing::System.Drawing.Color Page = FrameworkDrawing::System.Drawing.Color.FromArgb(21, 27, 37);
        private static readonly FrameworkDrawing::System.Drawing.Color Panel = FrameworkDrawing::System.Drawing.Color.FromArgb(31, 40, 53);
        private static readonly FrameworkDrawing::System.Drawing.Color Input = FrameworkDrawing::System.Drawing.Color.FromArgb(39, 49, 63);
        private static readonly FrameworkDrawing::System.Drawing.Color Primary = FrameworkDrawing::System.Drawing.Color.FromArgb(55, 116, 221);
        private static readonly FrameworkDrawing::System.Drawing.Color Foreground = FrameworkDrawing::System.Drawing.Color.FromArgb(236, 241, 246);
        private static readonly FrameworkDrawing::System.Drawing.Color Muted = FrameworkDrawing::System.Drawing.Color.FromArgb(164, 177, 194);

        public CloudWindow()
        {
            Text = "VIHANGA Cloud — Aircraft registry";
            StartPosition = FormStartPosition.CenterParent;
            Width = 570;
            Height = 490;
            MinimumSize = new FrameworkDrawing::System.Drawing.Size(500, 440);

            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(13);
            grid.RowCount = 9;
            grid.ColumnCount = 2;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(grid);

            _server.Text = !string.IsNullOrEmpty(Program.AeroHelmCloudOrigin)
                ? Program.AeroHelmCloudOrigin : AeroHelmWorkspaceSettings.CloudOrigin();
            _server.Dock = DockStyle.Fill;
            _username.Text = "operator";
            _username.Dock = DockStyle.Fill;
            _password.UseSystemPasswordChar = true;
            _password.Dock = DockStyle.Fill;
            _login.Text = "Sign in & list aircraft";
            _refresh.Text = "Refresh aircraft";
            _connect.Text = "Connect view";
            _disconnect.Text = "Disconnect view";

            AddLine(grid, 0, "Cloud URL", _server);
            AddLine(grid, 1, "Username", _username);
            AddLine(grid, 2, "Password", _password);
            var loginRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false };
            loginRow.Controls.Add(_login);
            loginRow.Controls.Add(_refresh);
            _login.AutoSize = true;
            _refresh.AutoSize = true;
            grid.Controls.Add(loginRow, 1, 3);
            grid.Controls.Add(new Label { Text = "Live aircraft (read-only)", Dock = DockStyle.Fill }, 0, 4);
            _aircraft.Dock = DockStyle.Fill;
            grid.Controls.Add(_aircraft, 0, 5);
            grid.SetColumnSpan(_aircraft, 2);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            actions.Controls.Add(_connect);
            actions.Controls.Add(_disconnect);
            _connect.AutoSize = true;
            _disconnect.AutoSize = true;
            grid.Controls.Add(actions, 0, 6);
            grid.SetColumnSpan(actions, 2);

            var caveat = new Label
            {
                Text = "Connect opens a cloud telemetry-view session only. It does NOT connect Mission Planner's MAVLink stream or grant flight-control authority.",
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = FrameworkDrawing::System.Drawing.Color.DarkRed
            };
            grid.Controls.Add(caveat, 0, 7);
            grid.SetColumnSpan(caveat, 2);
            _status.Dock = DockStyle.Fill;
            _status.Text = "Not signed in";
            _status.AutoEllipsis = true;
            grid.Controls.Add(_status, 0, 8);
            grid.SetColumnSpan(_status, 2);

            _login.Click += async (s, e) => await LoginAsync();
            _refresh.Click += async (s, e) => await RefreshAsync();
            _connect.Click += async (s, e) => await ConnectAsync();
            _disconnect.Click += async (s, e) => await DisconnectAsync();
            _aircraft.SelectedIndexChanged += (s, e) => UpdateButtons();
            // Reuse the startup authentication session. No duplicate login.
            if (!string.IsNullOrEmpty(Program.AeroHelmOperatorToken) &&
                DateTime.UtcNow < Program.AeroHelmTokenExpiresUtc)
            {
                _baseUrl = Program.AeroHelmCloudOrigin;
                _accessToken = Program.AeroHelmOperatorToken;
                _tokenDeadline = Program.AeroHelmTokenExpiresUtc;
                _status.Text = "Authenticated at AeroHelm startup. Refreshing aircraft...";
                Shown += async (s,e) => { try { await RefreshInternalAsync(); }
                    catch (Exception ex) { _status.Text = ex.Message; }
                    finally { UpdateButtons(); } };
            }
            UpdateButtons();
        }

        private static void AddLine(TableLayoutPanel grid, int row, string caption, Control control)
        {
            grid.Controls.Add(new Label
            {
                Text = caption, Dock = DockStyle.Fill,
            }, 0, row);
            grid.Controls.Add(control, 1, row);
        }

        private bool Authenticated
        {
            get { return !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenDeadline; }
        }

        private void UpdateButtons()
        {
            _login.Enabled = !_busy;
            _refresh.Enabled = !_busy && Authenticated;
            bool selected = _aircraft.SelectedIndex >= 0 && _aircraft.SelectedIndex < _aircraftData.Count;
            bool onlineHardware = selected &&
                                  _aircraftData[_aircraft.SelectedIndex].source == "hardware" &&
                                  _aircraftData[_aircraft.SelectedIndex].connection == "online";
            _connect.Enabled = !_busy && Authenticated && onlineHardware && string.IsNullOrEmpty(_connectionId);
            _disconnect.Enabled = !_busy && Authenticated && !string.IsNullOrEmpty(_connectionId);
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            _status.Text = message;
            UpdateButtons();
        }

        private static Uri ParseServer(string text)
        {
            Uri url;
            if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out url))
                throw new InvalidOperationException("Enter an absolute cloud URL.");
            if (!(url.Scheme == Uri.UriSchemeHttps &&
                  (url.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
                   url.IsLoopback) ||
                  url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
                throw new InvalidOperationException("Cloud requires private Tailscale HTTPS. HTTP is allowed only on loopback.");
            if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) ||
                !string.IsNullOrEmpty(url.Fragment) || url.AbsolutePath != "/")
                throw new InvalidOperationException("Enter only the cloud origin; no path, query or credentials.");
            return url;
        }

        private static string Request(string origin, string route, string method, string token, string payload)
        {
            var url = new Uri(new Uri(origin + "/"), route.TrimStart('/'));
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.AllowAutoRedirect = false; // Never forward authorization to redirected hosts.
            req.Timeout = 12000;
            req.ReadWriteTimeout = 12000;
            req.Accept = "application/json";
            if (!string.IsNullOrEmpty(token))
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;

            if (payload != null)
            {
                var data = Encoding.UTF8.GetBytes(payload);
                req.ContentType = "application/json";
                req.ContentLength = data.Length;
                using (var stream = req.GetRequestStream())
                    stream.Write(data, 0, data.Length);
            }
            try
            {
                using (var response = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (code < 200 || code >= 300)
                        throw new InvalidOperationException("Cloud returned HTTP " + code);
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch (WebException e)
            {
                var response = e.Response as HttpWebResponse;
                if (response != null)
                {
                    int code = (int)response.StatusCode;
                    response.Dispose();
                    throw new InvalidOperationException("Cloud request failed (HTTP " + code + ").");
                }
                throw new InvalidOperationException("Cloud connection failed: " + e.Status);
            }
        }

        private async Task LoginAsync()
        {
            if (_busy) return;
            string origin, username, password;
            try
            {
                origin = ParseServer(_server.Text).GetLeftPart(UriPartial.Authority);
                username = _username.Text.Trim();
                password = _password.Text;
                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                    throw new InvalidOperationException("Username and password are required.");
            }
            catch (Exception e) { _status.Text = e.Message; return; }

            // Do not retain password in the UI beyond this request.
            _password.Text = "";
            _accessToken = null;
            _connectionId = null;
            _aircraftData.Clear();
            _aircraft.Items.Clear();
            SetBusy(true, "Signing in...");
            try
            {
                var payload = _json.Serialize(new { username = username, password = password });
                var result = await Task.Run(() => Request(origin, "/api/v1/auth/login", "POST", null, payload));
                var parsed = _json.Deserialize<LoginResult>(result);
                if (parsed == null || string.IsNullOrEmpty(parsed.access_token))
                    throw new InvalidOperationException("Cloud login response is invalid.");
                _baseUrl = origin;
                // Persist only the nonsecret HTTPS origin, never password or JWT.
                try { AeroHelmWorkspaceSettings.Save("cloud-url", origin); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                _accessToken = parsed.access_token;
                Program.AeroHelmOperatorToken = _accessToken;
                Program.AeroHelmCloudOrigin = origin;
                Program.AeroHelmTokenExpiresUtc = DateTime.UtcNow.AddSeconds(
                    Math.Max(30, parsed.expires_in - 30));
                _tokenDeadline = DateTime.UtcNow.AddSeconds(Math.Max(30, parsed.expires_in - 30));
                _status.Text = "Authenticated. Loading aircraft...";
                await RefreshInternalAsync();
            }
            catch (Exception e) { _status.Text = e.Message; }
            finally
            {
                password = null;
                SetBusy(false, _status.Text);
            }
        }

        private async Task RefreshAsync()
        {
            if (_busy || !Authenticated) return;
            SetBusy(true, "Refreshing aircraft...");
            try { await RefreshInternalAsync(); }
            catch (Exception e) { _status.Text = e.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private async Task RefreshInternalAsync()
        {
            if (!Authenticated)
                throw new InvalidOperationException("Sign in again; cloud session expired.");
            string body = await Task.Run(() => Request(_baseUrl, "/api/v1/vehicles", "GET", _accessToken, null));
            var devices = _json.Deserialize<List<AircraftInfo>>(body) ?? new List<AircraftInfo>();
            _aircraftData = devices;
            _aircraft.Items.Clear();
            foreach (var d in devices)
                _aircraft.Items.Add(d.vehicle_id + " | " + d.source + " | " + d.connection + " | " + (d.mode ?? "—"));
            _status.Text = devices.Count == 0
                ? "No live aircraft reported by the cloud. Start the Pi agent first."
                : devices.Count + " aircraft registered (live state may expire).";
        }

        private async Task ConnectAsync()
        {
            if (_busy || !Authenticated) return;
            int index = _aircraft.SelectedIndex;
            if (index < 0 || index >= _aircraftData.Count) return;
            var d = _aircraftData[index];
            if (d.source != "hardware" || d.connection != "online") return;
            SetBusy(true, "Creating cloud telemetry-view session...");
            try
            {
                string body = _json.Serialize(new { vehicle_id = d.vehicle_id });
                string response = await Task.Run(() => Request(_baseUrl, "/api/v1/connections", "POST", _accessToken, body));
                var session = _json.Deserialize<ConnectionInfo>(response);
                if (session == null || string.IsNullOrEmpty(session.connection_id))
                    throw new InvalidOperationException("Invalid cloud connection response.");
                _connectionId = session.connection_id;
                _status.Text = "VIEW session active for " + d.vehicle_id + " (not MAVLink connected).";
            }
            catch (Exception e) { _status.Text = e.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private async Task DisconnectAsync()
        {
            if (_busy || string.IsNullOrEmpty(_connectionId)) return;
            string session = _connectionId;
            SetBusy(true, "Closing view session...");
            try
            {
                await Task.Run(() => Request(_baseUrl, "/api/v1/connections/" + Uri.EscapeDataString(session), "DELETE", _accessToken, null));
                _connectionId = null;
                _status.Text = "Cloud telemetry view disconnected.";
            }
            catch (Exception e) { _status.Text = e.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _accessToken = null;
                _connectionId = null;
            }
            base.Dispose(disposing);
        }

        public sealed class LoginResult
        {
            public string access_token { get; set; }
            public int expires_in { get; set; }
        }

        public sealed class AircraftInfo
        {
            public string vehicle_id { get; set; }
            public string source { get; set; }
            public string connection { get; set; }
            public string mode { get; set; }
        }

        public sealed class ConnectionInfo
        {
            public string connection_id { get; set; }
        }
    }
}
