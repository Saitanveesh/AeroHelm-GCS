// AeroHelm operator workspace: SURVEY, VISION, LINK diagnostics.
// UI is independent of live MAVLink commands. GPL-3.0-or-later.
extern alias FrameworkDrawing;

using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal static class AeroHelmWorkspaceSettings
    {
        private static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AeroHelm"); }
        }

        public static string Get(string name)
        {
            try
            {
                string path = Path.Combine(Folder, name + ".txt");
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : "";
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        public static void Save(string name, string value)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, name + ".txt"),
                              value + Environment.NewLine, Encoding.UTF8);
        }

        public static Uri CheckedPrivateUrl(string input)
        {
            Uri result;
            if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out result) ||
                !string.IsNullOrEmpty(result.UserInfo) || !string.IsNullOrEmpty(result.Fragment) ||
                !string.IsNullOrEmpty(result.Query) ||
                !((result.Scheme == Uri.UriSchemeHttps &&
                   (result.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
                    result.IsLoopback)) ||
                  (result.Scheme == Uri.UriSchemeHttp && result.IsLoopback)))
                throw new ArgumentException("Use Tailscale HTTPS (*.ts.net) or HTTP(S) loopback for local simulation. Do not include credentials, queries or fragments.");
            return result;
        }

        public static string CloudOrigin()
        {
            var value = Get("cloud-url");
            return string.IsNullOrEmpty(value) ? "https://" : value;
        }
    }

    internal sealed class AeroHelmSurveyWindow : Form
    {
        private readonly NumericUpDown north = Coordinate();
        private readonly NumericUpDown south = Coordinate();
        private readonly NumericUpDown east = Coordinate();
        private readonly NumericUpDown west = Coordinate();
        private readonly NumericUpDown spacing = Number(5, 300, 30, 1);
        private readonly NumericUpDown altitude = Number(5, 120, 30, 1);
        private readonly NumericUpDown speed = Number(1, 15, 5, 1);
        private readonly DataGridView grid = new DataGridView();
        private readonly Label status = new Label();
        private readonly Button export = new Button();
        private SurveyDraft draft;

        private static NumericUpDown Coordinate()
        {
            return new NumericUpDown
            {
                Minimum = -180, Maximum = 180,
                DecimalPlaces = 6, Increment = .0001M, Width = 110
            };
        }

        private static NumericUpDown Number(int min, int max, int val, int dp)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, Value = val,
                DecimalPlaces = dp, Width = 80
            };
        }

        private static void Add(FlowLayoutPanel panel, string name, Control control)
        {
            panel.Controls.Add(new Label { Text = name, Width = 96,
                TextAlign = Drawing.ContentAlignment.MiddleRight, Height = 25 });
            panel.Controls.Add(control);
        }

        public AeroHelmSurveyWindow()
        {
            Text = "AeroHelm SURVEY — Offline autonomous mission draft";
            StartPosition = FormStartPosition.CenterParent;
            Width = 820;
            Height = 680;
            MinimumSize = new Drawing.Size(740, 520);

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 154, AutoScroll = true,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
                Padding = new Padding(8)
            };
            Add(top, "North latitude", north);
            Add(top, "South latitude", south);
            Add(top, "West longitude", west);
            Add(top, "East longitude", east);
            Add(top, "Row spacing (m)", spacing);
            Add(top, "Rel. altitude (m)", altitude);
            Add(top, "Speed (m/s)", speed);
            var generate = new Button { Text = "Generate survey draft", Width = 175, Height = 30 };
            export.Text = "Export .waypoints";
            export.Width = 145;
            export.Height = 30;
            export.Enabled = false;
            top.Controls.Add(generate);
            top.Controls.Add(export);

            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add("seq", "Sequence");
            grid.Columns.Add("lat", "Latitude");
            grid.Columns.Add("lon", "Longitude");
            grid.Columns.Add("alt", "Rel. altitude (m)");

            status.Dock = DockStyle.Bottom;
            status.Height = 88;
            status.Padding = new Padding(10);
            status.Text = "OFFLINE ONLY: enter the site coordinates and generate a draft. No takeoff, RTL, landing, mission upload or cloud command is issued.\nReview home, terrain, airspace, geofence, obstacles, mission transitions and battery margins before any flight.";
            status.ForeColor = Drawing.Color.DarkRed;

            Controls.Add(grid);
            Controls.Add(status);
            Controls.Add(top);

            generate.Click += (s, e) => Generate();
            export.Click += (s, e) => Export();

            // Never export an old flight path after the operator changes inputs.
            EventHandler invalidate = (s, e) =>
            {
                draft = null;
                export.Enabled = false;
                grid.Rows.Clear();
                status.Text = "Survey inputs changed. Regenerate and review the draft before export.\n" +
                              "No commands or mission uploads are available from SURVEY.";
            };
            foreach (var input in new[] { north, south, east, west, spacing, altitude, speed })
                input.ValueChanged += invalidate;
        }

        private void Generate()
        {
            draft = null;
            export.Enabled = false;
            grid.Rows.Clear();
            try
            {
                draft = SurveyPlanner.Generate(new SurveyOptions
                {
                    North = (double)north.Value, South = (double)south.Value,
                    West = (double)west.Value, East = (double)east.Value,
                    SpacingMetres = (double)spacing.Value,
                    RelativeAltitudeMetres = (double)altitude.Value,
                    CruiseMetresPerSecond = (double)speed.Value
                });
                foreach (var point in draft.Points)
                    grid.Rows.Add(point.Sequence.ToString(CultureInfo.InvariantCulture),
                                  point.Latitude.ToString("F7", CultureInfo.InvariantCulture),
                                  point.Longitude.ToString("F7", CultureInfo.InvariantCulture),
                                  point.RelativeAltitudeMetres.ToString("F1", CultureInfo.InvariantCulture));
                status.Text = string.Format(CultureInfo.InvariantCulture,
                    "DRAFT READY — {0} waypoints, {1:F1} km route, nominal travel {2:F1} min (no takeoff/landing time).\n" +
                    "NOT UPLOADED. Mission Planner will overwrite placeholder home (row 0). Verify GPS, RTL, obstacles, airspace, terrain and battery before use.",
                    draft.Points.Count, draft.HorizontalDistanceMetres / 1000.0,
                    draft.NominalTravelSeconds / 60.0);
                export.Enabled = true;
            }
            catch (ArgumentException ex)
            {
                status.Text = "Validation failed: " + ex.Message;
            }
        }

        private void Export()
        {
            if (draft == null) return;
            using (var dialog = new SaveFileDialog
            {
                Title = "Export OFFLINE waypoint draft (never uploaded)",
                Filter = "Mission Planner waypoints (*.waypoints)|*.waypoints|Text (*.txt)|*.txt",
                FileName = "aerohelm-survey-DRAFT.waypoints"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SurveyPlanner.SaveDraft(draft, dialog.FileName);
                    status.Text = "Exported draft to: " + dialog.FileName +
                        "\nReview it in Mission Planner Flight Plan -> Load WP File. This tool does NOT upload, arm or start a mission.";
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    status.Text = "Export failed: " + ex.Message;
                }
            }
        }
    }

    // The camera page is a launcher, not an invented live video stream.
    // Camera 2 is configurable independently and is UNKNOWN until Pi testing.
    internal sealed class AeroHelmVisionWindow : Form
    {
        private readonly ComboBox camera = new ComboBox();
        private readonly TextBox url = new TextBox();
        private readonly Label status = new Label();
        private readonly Label saved = new Label();

        public AeroHelmVisionWindow()
        {
            Text = "AeroHelm VISION — Private cameras";
            Width = 760;
            Height = 405;
            MinimumSize = new Drawing.Size(620,360);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Drawing.Color.FromArgb(8,8,8);
            ForeColor = Drawing.Color.White;
            Font = new Drawing.Font("Segoe UI",11);
            KeyPreview = true;

            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, Padding = new Padding(20),
                ColumnCount = 1, RowCount = 6
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,49));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            Controls.Add(layout);
            var visionHeading = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1
            };
            visionHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,75));
            visionHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
            visionHeading.Controls.Add(new Label {
                Text="VISION  /  PRIVATE CAMERA VIEWERS",
                Dock=DockStyle.Fill,
                Font=new Drawing.Font("Segoe UI Semibold",16,Drawing.FontStyle.Bold),
                ForeColor=Drawing.Color.White,
                TextAlign=Drawing.ContentAlignment.MiddleLeft
            },0,0);
            var visionHome = new Button {
                Text = "← HOME", Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Drawing.Color.FromArgb(38,38,38),
                ForeColor = Drawing.Color.White
            };
            visionHome.FlatAppearance.BorderSize = 0;
            visionHome.Click += (s,e) => Close();
            visionHeading.Controls.Add(visionHome,1,0);
            layout.Controls.Add(visionHeading,0,0);

            camera.DropDownStyle=ComboBoxStyle.DropDownList;
            camera.Items.AddRange(new object[] {"CAMERA 1 — Primary","CAMERA 2 — Optional"});
            camera.SelectedIndex=0;
            camera.Dock=DockStyle.Fill;
            camera.BackColor=Drawing.Color.FromArgb(38,38,38);
            camera.ForeColor=Drawing.Color.White;
            camera.SelectedIndexChanged+=(s,e)=>LoadCamera();
            layout.Controls.Add(camera,0,1);

            url.Dock=DockStyle.Fill;
            url.BackColor=Drawing.Color.FromArgb(32,32,32);
            url.ForeColor=Drawing.Color.White;
            url.BorderStyle=BorderStyle.FixedSingle;
            url.AccessibleName="Selected camera private HTTPS address";
            layout.Controls.Add(url,0,2);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true};
            var open=new Button {Text="OPEN CAMERA IN BROWSER",Width=235,Height=42};
            var save=new Button {Text="SAVE PRIVATE URL",Width=190,Height=42};
            foreach(var button in new[]{open,save})
            {
                button.FlatStyle=FlatStyle.Flat;
                button.FlatAppearance.BorderSize=0;
                button.BackColor=Drawing.Color.FromArgb(38,38,38);
                button.ForeColor=Drawing.Color.White;
                button.Margin=new Padding(0,0,10,0);
                actions.Controls.Add(button);
            }
            open.Click+=(s,e)=>Open();
            save.Click+=(s,e)=>Save();
            layout.Controls.Add(actions,0,3);

            saved.Dock=DockStyle.Fill;
            saved.ForeColor=Drawing.Color.FromArgb(186,186,186);
            layout.Controls.Add(saved,0,4);
            status.Dock=DockStyle.Fill;
            status.ForeColor=Drawing.Color.FromArgb(196,196,196);
            status.Text="Streaming is hosted on the Raspberry Pi / private network, not Oracle."+
                Environment.NewLine+"This page opens the actual private camera server; it does not simulate video."+
                Environment.NewLine+"Camera 2 is not considered available until separately tested on hardware.";
            layout.Controls.Add(status,0,5);
            LoadCamera();
            // Reopening the VISION tile opens the known camera immediately.
            // Video still travels directly over private Tailscale/WebRTC.
            if (string.Equals(Environment.GetEnvironmentVariable(
                    "AEROHELM_VISION_AUTO_OPEN"),"1",StringComparison.Ordinal))
                Shown += (s,e) => BeginInvoke(new Action(() => {
                    if (camera.SelectedIndex == 0 && !string.IsNullOrWhiteSpace(url.Text))
                        Open();
                }));
            KeyDown+=(s,e)=>{
                if(e.KeyCode==Keys.Escape){Close();e.Handled=true;}
            };
        }

        private string Key
        {
            get { return camera.SelectedIndex==1 ? "vision-url-2" : "vision-url"; }
        }

        private void LoadCamera()
        {
            url.Text=AeroHelmWorkspaceSettings.Get(Key);
            bool suggested = false;
            // The session launcher provides the already-established Pi address.
            // Never construct a public or unauthenticated camera endpoint.
            if (camera.SelectedIndex == 0 && string.IsNullOrWhiteSpace(url.Text))
            {
                var proposed = Environment.GetEnvironmentVariable("AEROHELM_CAMERA1_URL");
                if (!string.IsNullOrWhiteSpace(proposed))
                {
                    try
                    {
                        var address = AeroHelmWorkspaceSettings.CheckedPrivateUrl(proposed);
                        if (address.Scheme == Uri.UriSchemeHttps &&
                            address.Host.EndsWith(".ts.net",StringComparison.OrdinalIgnoreCase))
                        {
                            url.Text = address.AbsoluteUri;
                            suggested = true;
                        }
                    }
                    catch (ArgumentException) { /* Invalid URL stays blank. */ }
                }
            }
            saved.Text = string.IsNullOrWhiteSpace(url.Text) ?
                "Local configuration: NOT CONFIGURED" : suggested ?
                "Private Camera 1 URL prefilled; live stream still unverified." :
                "Local configuration: address on file (stream availability UNKNOWN)";
        }

        private Uri Validate()
        {
            if(string.IsNullOrWhiteSpace(url.Text))
                throw new ArgumentException("Configure the camera's private address first.");
            var address=AeroHelmWorkspaceSettings.CheckedPrivateUrl(url.Text);
            // Loopback HTTP is retained only for isolated bench/video development.
            if(address.Scheme!=Uri.UriSchemeHttps && !address.IsLoopback)
                throw new ArgumentException("Camera must use private Tailscale HTTPS.");
            return address;
        }

        private bool Save()
        {
            try
            {
                var address=Validate();
                AeroHelmWorkspaceSettings.Save(Key,address.AbsoluteUri);
                saved.Text="Saved locally. Actual camera stream status remains UNKNOWN until verified.";
                return true;
            }
            catch(Exception ex) when(ex is ArgumentException || ex is IOException ||
                                      ex is UnauthorizedAccessException)
            {
                status.Text="Camera configuration rejected: "+ex.Message;
                return false;
            }
        }

        private void Open()
        {
            if(!Save())return;
            try
            {
                var address=Validate();
                Process.Start(new ProcessStartInfo {
                    FileName=address.AbsoluteUri, UseShellExecute=true
                });
                status.Text="Opened selected private camera page in your browser."+
                    Environment.NewLine+"No stream is assumed active until the Pi server responds."+
                    Environment.NewLine+"Return here to select the other camera.";
            }
            catch(Exception ex) when(ex is System.ComponentModel.Win32Exception ||
                                      ex is InvalidOperationException || ex is ArgumentException)
            {
                status.Text="Cannot open private camera page: "+ex.Message;
            }
        }
    }

    internal sealed class AeroHelmLinkWindow : Form
    {
        private readonly TextBox url = new TextBox();
        private readonly TextBox output = new TextBox();
        private readonly Button check = new Button();
        private readonly Label freshness = new Label();
        private readonly CheckBox autoRefresh = new CheckBox();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private bool checking;

        public AeroHelmLinkWindow()
        {
            Text = "AeroHelm LINK — Cloud link diagnostics";
            Width = 770; Height = 470;
            MinimumSize = new Drawing.Size(620,400);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Drawing.Color.FromArgb(8,8,8);
            ForeColor = Drawing.Color.White;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15),
                RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            var linkHeading = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1
            };
            linkHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,75));
            linkHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
            freshness.Dock = DockStyle.Fill;
            freshness.Text = "LINK / UNKNOWN — no connection check has run";
            freshness.ForeColor = Drawing.Color.FromArgb(210,210,210);
            freshness.Font = new Drawing.Font("Segoe UI Semibold",15,Drawing.FontStyle.Bold);
            freshness.TextAlign = Drawing.ContentAlignment.MiddleLeft;
            linkHeading.Controls.Add(freshness,0,0);
            var linkHome = new Button {
                Text = "← HOME", Dock = DockStyle.Fill,
                BackColor = Drawing.Color.FromArgb(38,38,38),
                ForeColor = Drawing.Color.White,
                FlatStyle = FlatStyle.Flat
            };
            linkHome.FlatAppearance.BorderSize = 0;
            linkHome.Click += (s,e) => Close();
            linkHeading.Controls.Add(linkHome,1,0);
            layout.Controls.Add(linkHeading,0,0);
            url.Text = AeroHelmWorkspaceSettings.CloudOrigin();
            url.Dock = DockStyle.Fill;
            url.BackColor = Drawing.Color.FromArgb(34,34,34);
            url.ForeColor = Drawing.Color.White;
            layout.Controls.Add(url, 0, 1);
            check.Text = "CHECK CLOUD + MAVLINK";
            check.Width = 225; check.Height = 41;
            check.FlatStyle = FlatStyle.Flat;
            check.FlatAppearance.BorderSize = 0;
            check.BackColor = Drawing.Color.FromArgb(38,38,38);
            check.ForeColor = Drawing.Color.White;
            check.Click += async (s,e) => await CheckAsync();
            autoRefresh.Text = "Monitor every 15 seconds (read only)";
            autoRefresh.AutoSize = true;
            autoRefresh.ForeColor = Drawing.Color.FromArgb(210,210,210);
            autoRefresh.Margin = new Padding(12,11,0,0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            buttons.Controls.Add(check);
            buttons.Controls.Add(autoRefresh);
            layout.Controls.Add(buttons, 0, 2);
            refreshTimer.Interval = 15000;
            refreshTimer.Tick += async (s,e) => await CheckAsync();
            autoRefresh.CheckedChanged += (s,e) => {
                if (autoRefresh.Checked) refreshTimer.Start();
                else refreshTimer.Stop();
            };
            output.Dock = DockStyle.Fill;
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.Font = new Drawing.Font("Consolas", 10);
            output.Text = "No test has run. Link diagnostics are informational and do not establish flight safety.";
            output.BackColor = Drawing.Color.FromArgb(25,25,25);
            output.ForeColor = Drawing.Color.FromArgb(230,230,230);
            output.BorderStyle = BorderStyle.None;
            layout.Controls.Add(output, 0, 3);
            FormClosing += (s,e) => refreshTimer.Stop();
        }

        private static string Read(Uri origin, string route)
        {
            var target = new Uri(origin, route);
            var request = (HttpWebRequest)WebRequest.Create(target);
            request.Timeout = 8000;
            request.AllowAutoRedirect = false;
            request.Accept = "application/json";
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                if ((int)response.StatusCode != 200)
                    throw new WebException("Cloud returned HTTP " + (int)response.StatusCode);
                return reader.ReadToEnd();
            }
        }

        private async Task CheckAsync()
        {
            if (checking || IsDisposed || Disposing) return;
            checking = true;
            check.Enabled = false;
            freshness.Text = "LINK / CHECKING — no data confirmed";
            freshness.ForeColor = Drawing.Color.FromArgb(212,212,212);
            output.Text = "Connecting to private AeroHelm Cloud...";
            try
            {
                var address = AeroHelmWorkspaceSettings.CheckedPrivateUrl(url.Text);
                if (address.AbsolutePath != "/")
                    throw new ArgumentException("Enter cloud origin only, without a path.");
                var origin = new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
                var result = await Task.Run(() =>
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var health = Read(origin, "health");
                    timer.Stop();
                    var apiMs = timer.ElapsedMilliseconds;
                    timer.Restart();
                    var mavlink = Read(origin, "api/v1/mavlink/health");
                    timer.Stop();
                    return new { health, mavlink, apiMs, linkMs=timer.ElapsedMilliseconds };
                });
                var json = new JavaScriptSerializer();
                var h = json.Deserialize<Dictionary<string, object>>(result.health);
                var m = json.Deserialize<Dictionary<string, object>>(result.mavlink);
                if (IsDisposed || Disposing) return;
                AeroHelmWorkspaceSettings.Save("cloud-url", origin.GetLeftPart(UriPartial.Authority));
                bool fresh = m.ContainsKey("fresh") && m["fresh"] is bool && (bool)m["fresh"];
                freshness.Text = fresh ? "LINK / FRESH TELEMETRY (CLOUD REPORTED)" :
                                         "LINK / STALE OR NO PIXHAWK STREAM";
                freshness.ForeColor = fresh ? Drawing.Color.FromArgb(196,236,196) :
                                              Drawing.Color.FromArgb(255,184,150);
                output.Text =
                    "Cloud: " + Convert.ToString(h["status"]) + Environment.NewLine +
                    "Mode: " + Convert.ToString(h["mode"]) + Environment.NewLine +
                    "Aircraft: " + Convert.ToString(m["vehicle_id"]) + Environment.NewLine +
                    "Pi device connected: " + Convert.ToString(m["device_online"]) + Environment.NewLine +
                    "Fresh MAVLink: " + Convert.ToString(m["fresh"]) + Environment.NewLine +
                    "Total frames: " + Convert.ToString(m["frames_received"]) + Environment.NewLine +
                    "GCS viewers: " + Convert.ToString(m["viewers"]) + Environment.NewLine +
                    "API HTTP response: " + result.apiMs.ToString(CultureInfo.InvariantCulture) + " ms" + Environment.NewLine +
                    "Link endpoint response: " + result.linkMs.ToString(CultureInfo.InvariantCulture) + " ms" + Environment.NewLine +
                    "Frame rate (5s): " + Convert.ToString(m.ContainsKey("frame_rate_5s") ? m["frame_rate_5s"] : "server update required") + Environment.NewLine +
                    "Heartbeat age: " + Convert.ToString(m.ContainsKey("heartbeat_age_ms") ? m["heartbeat_age_ms"] : "unknown") + " ms" + Environment.NewLine +
                    "Dropped queued frames: " + Convert.ToString(m.ContainsKey("viewer_frames_dropped") ? m["viewer_frames_dropped"] : "unknown") + Environment.NewLine +
                    "UTC checked: " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture) +
                    Environment.NewLine + Environment.NewLine +
                    "Diagnostics only. Fresh telemetry is not proof of safe flight or command authority.";
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                freshness.Text = "LINK / UNAVAILABLE — NO VERIFIED TELEMETRY";
                freshness.ForeColor = Drawing.Color.FromArgb(255,184,150);
                output.Text = "Link check failed: " + ex.Message +
                    "\nCheck Tailscale, cloud API, hostname and TLS. No certificate checks were disabled.";
            }
            finally
            {
                checking = false;
                if (!IsDisposed && !Disposing) check.Enabled = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) refreshTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
