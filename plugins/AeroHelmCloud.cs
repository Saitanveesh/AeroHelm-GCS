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
        private bool _simulationWasVisible;
        private bool _helpWasVisible;

        public override string Name { get { return "AeroHelm Cloud"; } }
        public override string Version { get { return "0.1.1"; } }
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
            _button.DisplayStyle = ToolStripItemDisplayStyle.Text;
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

            // AeroHelm operational profile: simplify the toolbar only.
            // Do not delete Simulation or Help functionality, which upstream
            // may still need for diagnosis, SITL and maintenance.
            _simulationWasVisible = main.MenuSimulation.Visible;
            _helpWasVisible = main.MenuHelp.Visible;
            main.MenuSimulation.Visible = false;
            main.MenuHelp.Visible = false;
            return true;
        }

        public override bool Exit()
        {
            if (_button != null)
            {
                var main = MainV2.instance;
                if (main != null && !main.IsDisposed)
                {
                    main.MenuSimulation.Visible = _simulationWasVisible;
                    main.MenuHelp.Visible = _helpWasVisible;
                    main.MainMenu.Items.Remove(_button);
                }
                _button.Dispose();
                _button = null;
            }
            return true;
        }
    }

    // This dialog owns its colors; Mission Planner's legacy theme would
    // otherwise repaint buttons in bright green and obscure status text.
    [MissionPlanner.Utilities.PreventTheming]
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
            Text = "AeroHelm Cloud";
            StartPosition = FormStartPosition.CenterParent;
            Width = 760;
            Height = 660;
            MinimumSize = new FrameworkDrawing::System.Drawing.Size(660, 590);
            BackColor = Page;
            ForeColor = Foreground;
            Font = new FrameworkDrawing::System.Drawing.Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Page,
                ColumnCount = 1,
                RowCount = 7,
                Padding = new Padding(23, 14, 23, 14)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 77));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 199));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            Controls.Add(root);

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Page, RowCount = 2, ColumnCount = 1
            };
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            header.Controls.Add(MakeLabel("AeroHelm Cloud", 17f, true, Foreground), 0, 0);
            header.Controls.Add(MakeLabel("Secure aircraft registry and telemetry viewing", 9f, false, Muted), 0, 1);
            root.Controls.Add(header, 0, 0);

            var loginPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Panel,
                ColumnCount = 2, RowCount = 5, Padding = new Padding(15, 10, 15, 8)
            };
            loginPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 127));
            loginPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            loginPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            loginPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            loginPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            loginPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            loginPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            var connectionHeading = MakeLabel("CLOUD CONNECTION", 9f, true, Muted);
            loginPanel.Controls.Add(connectionHeading, 0, 0);
            loginPanel.SetColumnSpan(connectionHeading, 2);

            PrepareInput(_server);
            PrepareInput(_username);
            PrepareInput(_password);
            _server.Text = "https://";
            _username.Text = "operator";
            _password.UseSystemPasswordChar = true;
            AddLine(loginPanel, 1, "Server URL", _server);
            AddLine(loginPanel, 2, "Operator", _username);
            AddLine(loginPanel, 3, "Password", _password);

            _login.Text = "Sign in";
            _refresh.Text = "Refresh aircraft";
            _connect.Text = "Open telemetry view";
            _disconnect.Text = "Close view";
            PrepareButton(_login, true, 117);
            PrepareButton(_refresh, false, 155);
            PrepareButton(_connect, true, 184);
            PrepareButton(_disconnect, false, 122);
            var authActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Panel,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0), Padding = new Padding(0, 3, 0, 0)
            };
            authActions.Controls.Add(_login);
            authActions.Controls.Add(_refresh);
            loginPanel.Controls.Add(authActions, 1, 4);
            root.Controls.Add(loginPanel, 0, 1);

            var registry = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Page,
                ColumnCount = 2, RowCount = 1
            };
            registry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            registry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
            registry.Controls.Add(MakeLabel("AIRCRAFT REGISTRY", 9f, true, Muted), 0, 0);
            registry.Controls.Add(MakeLabel("Hardware telemetry only", 9f, false, Muted), 1, 0);
            root.Controls.Add(registry, 0, 2);

            _aircraft.Dock = DockStyle.Fill;
            _aircraft.Margin = new Padding(0, 0, 0, 8);
            _aircraft.BackColor = Input;
            _aircraft.ForeColor = Foreground;
            _aircraft.BorderStyle = BorderStyle.FixedSingle;
            _aircraft.Font = new FrameworkDrawing::System.Drawing.Font("Consolas", 10f);
            _aircraft.IntegralHeight = false;
            _aircraft.HorizontalScrollbar = true;
            _aircraft.ItemHeight = 26;
            _aircraft.DrawMode = DrawMode.OwnerDrawFixed;
            _aircraft.DrawItem += DrawAircraft;
            root.Controls.Add(_aircraft, 0, 3);

            var sessionActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Page,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0), Padding = new Padding(0, 7, 0, 0)
            };
            sessionActions.Controls.Add(_connect);
            sessionActions.Controls.Add(_disconnect);
            root.Controls.Add(sessionActions, 0, 4);

            var limitations = MakeLabel(
                "Telemetry view only — remote MAVLink connection, calibration and flight commands are not enabled.",
                9f, false, Muted);
            limitations.MaximumSize = new FrameworkDrawing::System.Drawing.Size(680, 0);
            root.Controls.Add(limitations, 0, 5);

            _status.Dock = DockStyle.Fill;
            _status.Margin = new Padding(0, 6, 0, 0);
            _status.Text = "Not signed in · No cloud session";
            _status.ForeColor = Muted;
            _status.BackColor = Page;
            _status.AutoEllipsis = true;
            root.Controls.Add(_status, 0, 6);

            _login.Click += async (s, e) => await LoginAsync();
            _refresh.Click += async (s, e) => await RefreshAsync();
            _connect.Click += async (s, e) => await ConnectAsync();
            _disconnect.Click += async (s, e) => await DisconnectAsync();
            _aircraft.SelectedIndexChanged += (s, e) => UpdateButtons();
            UpdateButtons();
        }

        private static Label MakeLabel(string content, float size, bool bold, FrameworkDrawing::System.Drawing.Color color)
        {
            return new Label
            {
                Text = content,
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = color,
                BackColor = FrameworkDrawing::System.Drawing.Color.Transparent,
                Font = new FrameworkDrawing::System.Drawing.Font("Segoe UI", size,
                    bold ? FrameworkDrawing::System.Drawing.FontStyle.Bold : FrameworkDrawing::System.Drawing.FontStyle.Regular)
            };
        }

        private static void PrepareInput(TextBox control)
        {
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 2, 0, 3);
            control.BackColor = Input;
            control.ForeColor = Foreground;
            control.BorderStyle = BorderStyle.FixedSingle;
            control.Font = new FrameworkDrawing::System.Drawing.Font("Segoe UI", 10f);
        }

        private static void PrepareButton(Button button, bool primary, int width)
        {
            button.Size = new FrameworkDrawing::System.Drawing.Size(width, 32);
            button.Margin = new Padding(0, 0, 10, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = FrameworkDrawing::System.Drawing.Color.FromArgb(89, 103, 124);
            button.BackColor = primary ? Primary : Panel;
            button.ForeColor = Foreground;
            button.Font = new FrameworkDrawing::System.Drawing.Font("Segoe UI", 9f, FrameworkDrawing::System.Drawing.FontStyle.Bold);
        }

        private static void AddLine(TableLayoutPanel grid, int row, string caption, Control control)
        {
            grid.Controls.Add(new Label
            {
                Text = caption, Dock = DockStyle.Fill,
                ForeColor = Muted, BackColor = FrameworkDrawing::System.Drawing.Color.Transparent
            }, 0, row);
            grid.Controls.Add(control, 1, row);
        }

        private void DrawAircraft(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _aircraft.Items.Count)
                return;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var background = new FrameworkDrawing::System.Drawing.SolidBrush(selected ? Primary : Input))
                e.Graphics.FillRectangle(background, e.Bounds);
            System.Windows.Forms.TextRenderer.DrawText(
                e.Graphics,
                _aircraft.Items[e.Index].ToString(),
                _aircraft.Font, e.Bounds, Foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
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
            if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
                throw new InvalidOperationException("Cloud requires HTTPS. HTTP is allowed only for loopback development.");
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
                _accessToken = parsed.access_token;
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
