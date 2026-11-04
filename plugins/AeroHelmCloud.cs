// AeroHelm Cloud — isolated Mission Planner plugin (first integration milestone).
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
        private ToolStripButton _button;
        private ToolStripButton _survey;
        private ToolStripButton _vision;
        private ToolStripButton _link;
        private ToolStripButton _ops;
        private ToolStripButton _intel;
        private ToolStripButton _touch;
        internal static AeroHelmReceiveOnlyBridge ActiveNativeBridge;
        private EventHandler _touchStartupShown;
        private bool _simulationWasVisible;
        private bool _helpWasVisible;

        public override string Name { get { return "AeroHelm Cloud"; } }
        public override string Version { get { return "0.2.0"; } }
        public override string Author { get { return "AeroHelm Contributors"; } }

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            // Mission Planner uses an icon-oriented MenuStrip with the COM selectors
            // and ArduPilot logo aligned to the right. Appending a ToolStripMenuItem
            // after those items can leave it hidden in the overflow area.
            // Place a distinct, text-only button immediately after HELP instead.
            _button = new ToolStripButton("CLOUD");
            _button.Name = "AeroHelmCloudButton";
            _button.ToolTipText = "AeroHelm Cloud: login, aircraft registry and view sessions (no flight commands)";
            _button.Image = AeroHelmIconPainter.Make("CLOUD", 20);
            _button.ImageScaling = ToolStripItemImageScaling.None;
            _button.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            _button.ForeColor = FrameworkDrawing::System.Drawing.Color.White;
            _button.Margin = new Padding(5, 0, 5, 0);
            _button.AutoSize = true;
            _button.Click += delegate
            {
                using (var window = new CloudWindow())
                    window.ShowDialog(MainV2.instance);
            };

            var main = MainV2.instance;
            var menu = main.MainMenu;
            var helpIndex = menu.Items.IndexOf(main.MenuHelp);
            menu.Items.Insert(helpIndex >= 0 ? helpIndex + 1 : 0, _button);

            // The additional AeroHelm tools are deliberately passive:
            // SURVEY exports offline drafts, VISION opens a private camera page,
            // LINK reads diagnostic status. None forwards MAVLink flight commands.
            int at = menu.Items.IndexOf(_button) + 1;
            _survey = NewToolbarButton("SURVEY", "Offline survey draft + route safety checks");
            _survey.Click += delegate
            {
                using (var window = new AeroHelmSurveyWindow())
                    window.ShowDialog(MainV2.instance);
            };
            menu.Items.Insert(at++, _survey);

            _vision = NewToolbarButton("VISION", "Private onboard camera viewer");
            _vision.Image = AeroHelmIconPainter.Make("VISION",20);
            _vision.ImageScaling = ToolStripItemImageScaling.None;
            _vision.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            _vision.Click += delegate
            {
                using (var window = new AeroHelmVisionWindow())
                    window.ShowDialog(MainV2.instance);
            };
            menu.Items.Insert(at++, _vision);

            _link = NewToolbarButton("LINK", "Check cloud telemetry freshness and viewer count");
            _link.Click += delegate
            {
                using (var window = new AeroHelmLinkWindow())
                    window.ShowDialog(MainV2.instance);
            };
            menu.Items.Insert(at++, _link);

            _ops = NewToolbarButton("OPS", "Simulation mission execution and supervised bench calibration");
            _ops.Click += delegate
            {
                using (var window = new AeroHelmOpsWindow())
                    window.ShowDialog(MainV2.instance);
            };
            menu.Items.Insert(at++, _ops);

            _intel = NewToolbarButton("INTEL", "Fleet, mission assurance, parameter drift, history and security");
            _intel.Click += delegate
            {
                using (var window = new AeroHelmIntelWindow())
                    window.ShowDialog(MainV2.instance);
            };
            menu.Items.Insert(at++, _intel);

            // Single touch-first entry point for nine actual workspaces.
            // Existing classic buttons are retained as a fallback for
            // mouse/keyboard operators and older Mission Planner workflows.
            _touch = NewToolbarButton("TOUCH", "Open nine-module AeroHelm touch workspace");
            _touch.Font = new FrameworkDrawing::System.Drawing.Font(
                "Segoe UI Semibold", 10f, FrameworkDrawing::System.Drawing.FontStyle.Bold);
            _touch.Click += delegate
            {
                using (var panel = new AeroHelmTouchPanel())
                    panel.ShowDialog(MainV2.instance);
            };
            menu.Items.Insert(menu.Items.IndexOf(_button) + 1, _touch);

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

            // Dedicated Windows tablet/kiosk mode is opt-in. Native Mission
            // Planner still starts normally first, including existing
            // AeroHelm operator login and all radio/USB recovery paths.
            if (string.Equals(Environment.GetEnvironmentVariable("AEROHELM_TOUCH_STARTUP"),
                              "1", StringComparison.Ordinal))
            {
                Action openTouch = () =>
                {
                    if (main.IsDisposed) return;
                    using (var panel = new AeroHelmTouchPanel())
                        panel.ShowDialog(main);
                };
                if (main.Visible && main.IsHandleCreated)
                {
                    main.BeginInvoke(openTouch);
                }
                else
                {
                    _touchStartupShown = (sender, args) =>
                    {
                        main.Shown -= _touchStartupShown;
                        _touchStartupShown = null;
                        if (!main.IsDisposed && main.IsHandleCreated)
                            main.BeginInvoke(openTouch);
                    };
                    main.Shown += _touchStartupShown;
                }
            }
            return true;
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
            if (ActiveNativeBridge != null)
            {
                ActiveNativeBridge.Dispose();
                ActiveNativeBridge = null;
            }
            if (_button != null)
            {
                var main = MainV2.instance;
                if (main != null && !main.IsDisposed)
                {
                    if (_touchStartupShown != null)
                    {
                        main.Shown -= _touchStartupShown;
                        _touchStartupShown = null;
                    }
                    main.MenuSimulation.Visible = _simulationWasVisible;
                    main.MenuHelp.Visible = _helpWasVisible;
                    main.MainMenu.Items.Remove(_button);
                    main.MainMenu.Items.Remove(_survey);
                    main.MainMenu.Items.Remove(_vision);
                    main.MainMenu.Items.Remove(_link);
                    main.MainMenu.Items.Remove(_ops);
                    main.MainMenu.Items.Remove(_intel);
                    main.MainMenu.Items.Remove(_touch);
                }
                // Every toolbar item owns its native bitmap. Release all
                // symbols when the plugin is unloaded; do not leak WinForms
                // GDI handles across repeated plugin lifecycle tests.
                foreach(var item in new ToolStripButton[]{
                    _button,_survey,_vision,_link,_ops,_intel,_touch
                })
                {
                    if(item.Image!=null)
                    {
                        var owned=item.Image;
                        item.Image=null;
                        owned.Dispose();
                    }
                    item.Dispose();
                }
                _button = null;
                _survey = null;
                _vision = null;
                _link = null;
                _ops = null;
                _intel = null;
                _touch = null;
            }
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
            Text = "AeroHelm Cloud — Aircraft registry";
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
