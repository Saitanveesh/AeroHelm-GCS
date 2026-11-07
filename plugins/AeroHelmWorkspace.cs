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
            Text = "VIHANGA SURVEY — Offline autonomous mission draft";
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
            Text = "VIHANGA VISION — Private cameras";
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
        private readonly Button duplexPing = new Button();
        private readonly Button duplexStatusQuery = new Button();
        private readonly Label duplexStatus = new Label();
        private readonly Label freshness = new Label();
        private readonly CheckBox autoRefresh = new CheckBox();
        private readonly CheckBox showDetails = new CheckBox();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
        private bool checking;
        private bool diagnosticPending;
        private string lastDuplexResult = "Not requested";
        private string lastSummary = "Link check has not run.";
        private string lastTechnical = "Link check has not run.";

        public AeroHelmLinkWindow()
        {
            Text = "Vihanga GCS | Link status";
            Width = 790; Height = 530;
            MinimumSize = new Drawing.Size(560,420);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Drawing.Color.FromArgb(8,8,8);
            ForeColor = Drawing.Color.White;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15),
                RowCount = 5, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            var linkHeading = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1
            };
            linkHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,75));
            linkHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
            freshness.Dock = DockStyle.Fill;
            freshness.Text = "Link | Not checked";
            freshness.ForeColor = Drawing.Color.FromArgb(210,210,210);
            freshness.Font = new Drawing.Font("Segoe UI Semibold",13,Drawing.FontStyle.Bold);
            freshness.AutoEllipsis = true;
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
            check.Text = "Check now";
            check.Width = 225; check.Height = 41;
            check.FlatStyle = FlatStyle.Flat;
            check.FlatAppearance.BorderSize = 0;
            check.BackColor = Drawing.Color.FromArgb(38,38,38);
            check.ForeColor = Drawing.Color.White;
            check.Click += async (s,e) => await CheckAsync();
            autoRefresh.Text = "Auto refresh (15 s)";
            autoRefresh.AutoSize = true;
            autoRefresh.ForeColor = Drawing.Color.FromArgb(210,210,210);
            autoRefresh.Margin = new Padding(12,11,0,0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
            buttons.Controls.Add(check);
            buttons.Controls.Add(autoRefresh);
            showDetails.Text = "Technical details";
            showDetails.AutoSize = true;
            showDetails.ForeColor = Drawing.Color.FromArgb(210,210,210);
            showDetails.Margin = new Padding(12,11,0,0);
            buttons.Controls.Add(showDetails);
            layout.Controls.Add(buttons, 0, 2);
            // Full duplex means authenticated diagnostic request/ACK only:
            // never forward GCS MAVLink, arm, calibrate, or change an FC parameter.
            var duplexBar = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true
            };
            duplexPing.Text = "Ping Pi";
            duplexPing.Width = 190; duplexPing.Height = 40;
            duplexStatusQuery.Text = "Pi counters";
            duplexStatusQuery.Width = 190; duplexStatusQuery.Height = 40;
            foreach (var b in new[] { duplexPing, duplexStatusQuery })
            {
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = Drawing.Color.FromArgb(45,45,45);
                b.ForeColor = Drawing.Color.White;
                b.FlatAppearance.BorderSize = 0;
                duplexBar.Controls.Add(b);
            }
            duplexPing.Click += async (s,e) => await ProbeDuplexAsync("ping");
            duplexStatusQuery.Click += async (s,e) => await ProbeDuplexAsync("uplink_status");
            duplexStatus.Width = 330; duplexStatus.Height = 40;
            duplexStatus.TextAlign = Drawing.ContentAlignment.MiddleLeft;
            duplexStatus.Text = "Pi diagnostics: not requested";
            duplexStatus.ForeColor = Drawing.Color.FromArgb(220,210,186);
            duplexBar.Controls.Add(duplexStatus);
            duplexBar.Visible = false;
            layout.Controls.Add(duplexBar,0,3);
            showDetails.CheckedChanged += (s,e) => {
                layout.RowStyles[3].Height = showDetails.Checked ? 84 : 0;
                duplexBar.Visible = showDetails.Checked;
                ShowSelectedReport();
            };
            refreshTimer.Interval = 15000;
            refreshTimer.Tick += async (s,e) => await CheckAsync();
            autoRefresh.CheckedChanged += (s,e) => {
                if (autoRefresh.Checked) refreshTimer.Start();
                else refreshTimer.Stop();
            };
            // Diagnostics are automatic in the AeroHelm profile. No cloud
            // settings or aircraft commands are changed by this polling.
            autoRefresh.Checked = true;
            output.Dock = DockStyle.Fill;
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Both;
            output.Font = new Drawing.Font("Consolas", 10);
            output.Text = lastSummary;
            output.BackColor = Drawing.Color.FromArgb(25,25,25);
            output.ForeColor = Drawing.Color.FromArgb(230,230,230);
            output.BorderStyle = BorderStyle.None;
            layout.Controls.Add(output, 0, 4);
            Shown += async (s,e) => await CheckAsync();
            FormClosing += (s,e) => refreshTimer.Stop();
        }

        private void ShowSelectedReport()
        {
            if(IsDisposed || Disposing) return;
            output.Text = showDetails.Checked ?
                lastTechnical + Environment.NewLine +
                "PI DIAGNOSTICS  " + lastDuplexResult : lastSummary;
        }

        // Do not send the operator JWT to an origin typed into LINK. Only the
        // process-login private origin may receive authenticated duplex POST.
        private void RequireAuthenticatedOrigin()
        {
            Uri displayed = AeroHelmWorkspaceSettings.CheckedPrivateUrl(url.Text);
            Uri authenticated = AeroHelmWorkspaceSettings.CheckedPrivateUrl(
                MissionPlanner.Program.AeroHelmCloudOrigin);
            if (displayed.AbsolutePath != "/" ||
                !string.Equals(displayed.GetLeftPart(UriPartial.Authority),
                    authenticated.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Duplex requires the signed-in private cloud origin.");
        }

        private async Task ProbeDuplexAsync(string action)
        {
            if (diagnosticPending || IsDisposed || Disposing) return;
            if (action != "ping" && action != "uplink_status") return;
            diagnosticPending = true;
            duplexPing.Enabled = duplexStatusQuery.Enabled = false;
            duplexStatus.Text = "DUPLEX / awaiting Pi ACK";
            try
            {
                RequireAuthenticatedOrigin();
                string summary = await Task.Run(() =>
                {
                    var json = new JavaScriptSerializer();
                    string submitted = OperationsApi.Request("POST",
                        "api/v1/network/requests",
                        new Dictionary<string,string> { { "action", action } });
                    var receipt = json.Deserialize<Dictionary<string,object>>(submitted);
                    string id = Convert.ToString(receipt["request_id"]);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(id,
                        @"^[0-9a-f]{32}$"))
                        throw new InvalidOperationException(
                            "Cloud returned an invalid diagnostic correlation ID.");
                    for (int i=0; i<22; i++)
                    {
                        string received = OperationsApi.Request("GET",
                            "api/v1/network/requests/" + id);
                        var report = json.Deserialize<Dictionary<string,object>>(received);
                        string state = Convert.ToString(report["status"]);
                        if (state != "PENDING")
                        {
                            if (state != "ACK")
                                return "DUPLEX / " + state + " — no trusted Pi result";
                            var details = report.ContainsKey("result") ?
                                report["result"] as Dictionary<string,object> : null;
                            if (details == null)
                                throw new InvalidOperationException("Missing Pi ACK details.");
                            string rtt = Convert.ToString(report["rtt_ms"],
                                CultureInfo.InvariantCulture);
                            if (action == "ping")
                            {
                                if (!details.ContainsKey("agent_alive") ||
                                    !(details["agent_alive"] is bool) ||
                                    !(bool)details["agent_alive"])
                                    throw new InvalidOperationException(
                                        "No positive Pi diagnostic acknowledgment.");
                                return "DUPLEX / ACK — Pi online; round-trip " + rtt + " ms";
                            }
                            return "DUPLEX / ACK — RTT " + rtt + " ms; frames " +
                                Convert.ToString(details["frames_observed"],
                                    CultureInfo.InvariantCulture) +
                                "; HB " + Convert.ToString(details["heartbeats_observed"],
                                    CultureInfo.InvariantCulture) +
                                "; ATT " + Convert.ToString(details["attitudes_observed"],
                                    CultureInfo.InvariantCulture) +
                                "; queued " + Convert.ToString(details["queued_frames"],
                                    CultureInfo.InvariantCulture) +
                                "; dropped " + Convert.ToString(details["dropped_frames"],
                                    CultureInfo.InvariantCulture);
                        }
                        System.Threading.Thread.Sleep(250);
                    }
                    return "DUPLEX / TIMED OUT — Pi did not ACK in the bounded window";
                });
                if (IsDisposed || Disposing) return;
                lastDuplexResult = summary;
                duplexStatus.Text = summary.StartsWith("DUPLEX / ACK",
                    StringComparison.Ordinal) ? "DUPLEX / ACK VERIFIED" : "DUPLEX / NO ACK";
                ShowSelectedReport();
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                // Do not print an exception message containing an HTTP response,
                // JWT, private device identity or user-supplied URL.
                lastDuplexResult = "DUPLEX / unavailable: " + ex.GetType().Name;
                duplexStatus.Text = "DUPLEX / UNAVAILABLE";
                ShowSelectedReport();
            }
            finally
            {
                diagnosticPending = false;
                if (!IsDisposed && !Disposing)
                    duplexPing.Enabled = duplexStatusQuery.Enabled = true;
            }
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
                    var linkMs = timer.ElapsedMilliseconds;
                    // Actual ATTITUDE is sensitive per-aircraft detail: use
                    // the authenticated, receive-only GCS API, NOT the public
                    // /health endpoint. The operator may inspect a different
                    // origin in LINK, so never send a JWT to that arbitrary URL.
                    string attitude = null;
                    string attitudeNote = "AUTHENTICATED PREVIEW UNAVAILABLE";
                    if (!string.IsNullOrWhiteSpace(MissionPlanner.Program.AeroHelmCloudOrigin) &&
                        string.Equals(origin.GetLeftPart(UriPartial.Authority),
                            MissionPlanner.Program.AeroHelmCloudOrigin,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        try {
                            attitude = OperationsApi.Request("GET","api/v1/mavlink/attitude");
                            attitudeNote = "OK";
                        }
                        catch (WebException ex) {
                            var http = ex.Response as HttpWebResponse;
                            attitudeNote = http != null && http.StatusCode == HttpStatusCode.NotFound ?
                                "M1 ATTITUDE API NOT DEPLOYED (HTTP 404)" :
                                http != null && (http.StatusCode == HttpStatusCode.Unauthorized ||
                                                 http.StatusCode == HttpStatusCode.Forbidden) ?
                                "SIGN IN AGAIN" : "CLOUD PREVIEW UNAVAILABLE";
                            if(http != null) http.Dispose();
                        }
                        catch (InvalidOperationException) {
                            attitudeNote = "SIGN IN AGAIN";
                        }
                    }
                    return new { health, mavlink, apiMs, linkMs,
                                 attitude, attitudeNote };
                });
                var json = new JavaScriptSerializer();
                var h = json.Deserialize<Dictionary<string, object>>(result.health);
                var m = json.Deserialize<Dictionary<string, object>>(result.mavlink);
                if (IsDisposed || Disposing) return;
                AeroHelmWorkspaceSettings.Save("cloud-url", origin.GetLeftPart(UriPartial.Authority));
                // A missing/old server endpoint is a known unverified state.
                // Roll/pitch are never inferred from generic frame counters.
                string attitudePreview = result.attitudeNote;
                if (result.attitude != null)
                {
                    var observation = json.Deserialize<Dictionary<string,object>>(
                        result.attitude);
                    object angleFresh;
                    bool validAngle = observation.TryGetValue("fresh",out angleFresh) &&
                                      angleFresh is bool && (bool)angleFresh;
                    if (validAngle && observation.ContainsKey("roll_deg") &&
                        observation.ContainsKey("pitch_deg"))
                        attitudePreview = "REAL roll " +
                            Convert.ToString(observation["roll_deg"],
                                CultureInfo.InvariantCulture) +
                            " deg / pitch " +
                            Convert.ToString(observation["pitch_deg"],
                                CultureInfo.InvariantCulture) +
                            " deg | age " +
                            Convert.ToString(observation["sample_age_ms"],
                                CultureInfo.InvariantCulture) + " ms";
                    else
                        attitudePreview = "STALE/MISSING — NO LIVE ATTITUDE";
                    object nativeReady;
                    if (!observation.TryGetValue("native_hud_ready",out nativeReady) ||
                        !(nativeReady is bool && (bool)nativeReady))
                        attitudePreview += " | HUD HEARTBEAT NOT VERIFIED";
                }
                bool fresh = m.ContainsKey("fresh") && m["fresh"] is bool && (bool)m["fresh"];
                object hbReady,attReady;
                bool? realHeartbeat = null;
                bool? realAttitude = null;
                if (m.TryGetValue("native_hud_heartbeat_ready",out hbReady) &&
                    hbReady is bool) realHeartbeat = (bool)hbReady;
                if (m.TryGetValue("attitude_stream_active",out attReady) &&
                    attReady is bool) realAttitude = (bool)attReady;
                var nativeEvidence = AeroHelmCloudPlugin.ActiveNativeBridge;
                bool localHb = nativeEvidence != null && nativeEvidence.NativeHeartbeatFresh;
                bool localAtt = nativeEvidence != null && nativeEvidence.NativeAttitudeFresh;
                if (!fresh)
                    freshness.Text = "Link | Telemetry stale";
                else if (realHeartbeat == false)
                    freshness.Text = "Link | Heartbeat missing";
                else if (realAttitude == false || (localHb && !localAtt))
                    freshness.Text = "Link | Attitude missing";
                else if (realHeartbeat == true && realAttitude == true)
                    freshness.Text = "Link | Heartbeat + attitude live";
                else
                    freshness.Text = "Link | Attitude unverified";
                freshness.ForeColor = (!fresh || realHeartbeat == false ||
                    realAttitude == false || (localHb && !localAtt)) ? Drawing.Color.FromArgb(255,184,150) :
                    realHeartbeat == true && realAttitude == true ?
                    Drawing.Color.FromArgb(196,236,196) :
                    Drawing.Color.FromArgb(243,211,166);
                var pi = m.ContainsKey("device_online") &&
                    m["device_online"] is bool && (bool)m["device_online"];
                string localSummary = nativeEvidence == null ? "Not attached" :
                    localHb ? (localAtt ? "HB + ATT live" :
                        "HB live / ATT missing") : "Heartbeat unavailable";
                string serverProfile = result.attitudeNote.IndexOf("HTTP 404",
                    StringComparison.OrdinalIgnoreCase)>=0 ?
                    "M1 legacy (attitude API absent)" :
                    result.attitudeNote=="OK" ? "Attitude API available" :
                    "Attitude API unverified";
                lastSummary =
                    "CLOUD     " + Convert.ToString(h["status"]) + Environment.NewLine +
                    "AIRCRAFT  " + Convert.ToString(m["vehicle_id"]) + Environment.NewLine +
                    "PI        " + (pi ? "Connected" : "Not connected") + Environment.NewLine +
                    "MAVLINK   " + (fresh ? "Receiving" : "Stale / missing") + Environment.NewLine +
                    "NATIVE    " + localSummary + Environment.NewLine +
                    "ATTITUDE  " + (localAtt ? "Fresh packets received" :
                        "No live roll/pitch (message 30)") + Environment.NewLine +
                    "SERVER    " + serverProfile + Environment.NewLine +
                    "LATENCY   API " + result.apiMs.ToString(CultureInfo.InvariantCulture) +
                        " ms / MAVLink " + result.linkMs.ToString(CultureInfo.InvariantCulture) +
                        " ms" + Environment.NewLine +
                    "UPDATED   " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'",
                        CultureInfo.InvariantCulture) + Environment.NewLine +
                    "MODE      Read-only";
                lastTechnical = lastSummary + Environment.NewLine + Environment.NewLine +
                    "SERVER COUNTERS" + Environment.NewLine +
                    "Frames: " + Convert.ToString(m.ContainsKey("frames_received") ?
                        m["frames_received"] : "Unavailable") +
                    " | viewers: " + Convert.ToString(m.ContainsKey("viewers") ?
                        m["viewers"] : "Unavailable") + Environment.NewLine +
                    "Frame rate (5 s): " + Convert.ToString(m.ContainsKey("frame_rate_5s") ?
                        m["frame_rate_5s"] : "Not reported") + Environment.NewLine +
                    "Heartbeat frames: " + Convert.ToString(m.ContainsKey("heartbeat_frames_received") ?
                        m["heartbeat_frames_received"] : "Not reported") + Environment.NewLine +
                    "Attitude frames: " + Convert.ToString(m.ContainsKey("attitude_frames_received") ?
                        m["attitude_frames_received"] : "Not reported") + Environment.NewLine +
                    "Attitude age (ms): " + Convert.ToString(m.ContainsKey("last_attitude_age_ms") ?
                        m["last_attitude_age_ms"] : "Not reported") + Environment.NewLine +
                    "Queued-frame drops: " + Convert.ToString(m.ContainsKey("viewer_frames_dropped") ?
                        m["viewer_frames_dropped"] : "Not reported") + Environment.NewLine +
                    "M1 attitude view: " + attitudePreview + Environment.NewLine +
                    "NATIVE BRIDGE" + Environment.NewLine +
                    (nativeEvidence == null ? "Unavailable" : nativeEvidence.Summary) +
                    Environment.NewLine +
                    (nativeEvidence == null ? "No local evidence" :
                        nativeEvidence.AttitudeTroubleshooting);
                ShowSelectedReport();
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                freshness.Text = "Link | Unavailable";
                freshness.ForeColor = Drawing.Color.FromArgb(255,184,150);
                lastSummary = "Cloud link unavailable. Check private HTTPS, sign-in and Pi service.";
                lastTechnical = lastSummary + Environment.NewLine +
                    "Error category: " + ex.GetType().Name;
                ShowSelectedReport();
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
