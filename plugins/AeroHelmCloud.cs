// AeroHelm Cloud — isolated Mission Planner plugin (first integration milestone).
// No MAVLink transport is installed or replaced by this plugin.
// Cloud "Connect" is an operator telemetry VIEW session, never flight authority.
// Requires HTTPS except loopback development. Credentials/tokens stay in memory.
// Mission Planner retains its original USB/TCP/UDP connection functionality.
// Copyright (C) AeroHelm contributors. GPL-3.0-or-later (see repository COPYING.txt).

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
        private ToolStripMenuItem _menu;

        public override string Name { get { return "AeroHelm Cloud"; } }
        public override string Version { get { return "0.1.0"; } }
        public override string Author { get { return "AeroHelm Contributors"; } }

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            _menu = new ToolStripMenuItem("AeroHelm Cloud");
            var show = new ToolStripMenuItem("Aircraft registry / view sessions");
            show.Click += delegate
            {
                using (var window = new CloudWindow())
                    window.ShowDialog(MainV2.instance);
            };
            _menu.DropDownItems.Add(show);
            MainV2.instance.MainMenu.Items.Add(_menu);
            return true;
        }

        public override bool Exit()
        {
            if (_menu != null)
            {
                MainV2.instance.MainMenu.Items.Remove(_menu);
                _menu.Dispose();
                _menu = null;
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

        public CloudWindow()
        {
            Text = "AeroHelm Cloud — Aircraft registry";
            StartPosition = FormStartPosition.CenterParent;
            Width = 570;
            Height = 490;
            MinimumSize = new System.Drawing.Size(500, 440);

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

            _server.Text = "https://";
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
            grid.Controls.Add(new Label { Text = "Live aircraft (read-only)", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 4);
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
                ForeColor = System.Drawing.Color.DarkRed
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
            UpdateButtons();
        }

        private static void AddLine(TableLayoutPanel grid, int row, string caption, Control control)
        {
            grid.Controls.Add(new Label
            {
                Text = caption, Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
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

        private sealed class LoginResult
        {
            public string access_token { get; set; }
            public int expires_in { get; set; }
        }

        private sealed class AircraftInfo
        {
            public string vehicle_id { get; set; }
            public string source { get; set; }
            public string connection { get; set; }
            public string mode { get; set; }
        }

        private sealed class ConnectionInfo
        {
            public string connection_id { get; set; }
        }
    }
}
