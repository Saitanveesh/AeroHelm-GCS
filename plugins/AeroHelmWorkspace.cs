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
                result.Scheme != Uri.UriSchemeHttps ||
                !(result.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
                  result.IsLoopback))
                throw new ArgumentException("Use a private Tailscale HTTPS (*.ts.net) URL or HTTPS loopback. Do not include credentials, query strings or fragments.");
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

    internal sealed class AeroHelmVisionWindow : Form
    {
        private readonly TextBox url = new TextBox();
        private readonly Label status = new Label();
        public AeroHelmVisionWindow()
        {
            Text = "AeroHelm VISION — Onboard camera";
            Width = 650; Height = 280;
            MinimumSize = new Drawing.Size(550, 240);
            StartPosition = FormStartPosition.CenterParent;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14),
                ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Camera 1 — Private WebRTC stream URL (Tailscale HTTPS only).",
                TextAlign = Drawing.ContentAlignment.MiddleLeft
            }, 0, 0);

            url.Dock = DockStyle.Fill;
            url.Text = AeroHelmWorkspaceSettings.Get("vision-url");
            layout.Controls.Add(url, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            var open = new Button { Text = "Open live video", Width = 145, Height = 28 };
            var save = new Button { Text = "Save camera address", Width = 155, Height = 28 };
            actions.Controls.Add(open);
            actions.Controls.Add(save);
            layout.Controls.Add(actions, 0, 2);
            status.Dock = DockStyle.Fill;
            status.Text = "Video requires a separate camera server on the Pi. The Oracle 1 GB VM is NOT a video relay.\n" +
                          "This launcher opens a trusted Tailscale HTTPS WebRTC page in the default browser; an embedded WebView2 player is not yet installed.";
            status.ForeColor = Drawing.Color.DarkRed;
            layout.Controls.Add(status, 0, 3);
            save.Click += (s, e) => Save();
            open.Click += (s, e) => Open();
        }

        private bool Save()
        {
            try
            {
                var address = AeroHelmWorkspaceSettings.CheckedPrivateUrl(url.Text);
                AeroHelmWorkspaceSettings.Save("vision-url", address.AbsoluteUri);
                status.Text = "Saved private camera URL locally (no token saved). Video server must be configured separately.";
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                                       ex is UnauthorizedAccessException)
            {
                status.Text = "Invalid camera URL: " + ex.Message;
                return false;
            }
        }

        private void Open()
        {
            if (!Save()) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url.Text.Trim(),
                    UseShellExecute = true
                });
                status.Text = "Opened your private video address in the default browser.\n" +
                              "Live video appears only if your Pi camera server and WebRTC path are running.";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception ||
                                       ex is InvalidOperationException)
            {
                status.Text = "Could not open browser: " + ex.Message;
            }
        }
    }

    internal sealed class AeroHelmLinkWindow : Form
    {
        private readonly TextBox url = new TextBox();
        private readonly TextBox output = new TextBox();
        private readonly Button check = new Button();

        public AeroHelmLinkWindow()
        {
            Text = "AeroHelm LINK — Cloud link diagnostics";
            Width = 660; Height = 410;
            StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12),
                RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            url.Text = AeroHelmWorkspaceSettings.CloudOrigin();
            url.Dock = DockStyle.Fill;
            layout.Controls.Add(url, 0, 0);
            check.Text = "Check cloud and MAVLink";
            check.Width = 180;
            check.Click += async (s,e) => await CheckAsync();
            layout.Controls.Add(check, 0, 1);
            output.Dock = DockStyle.Fill;
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.Font = new Drawing.Font("Consolas", 10);
            output.Text = "No test has run. Link diagnostics are informational and do not establish flight safety.";
            layout.Controls.Add(output, 0, 2);
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
            check.Enabled = false;
            output.Text = "Connecting to private AeroHelm Cloud...";
            try
            {
                var address = AeroHelmWorkspaceSettings.CheckedPrivateUrl(url.Text);
                if (address.AbsolutePath != "/")
                    throw new ArgumentException("Enter cloud origin only, without a path.");
                var origin = new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
                var result = await Task.Run(() =>
                {
                    var health = Read(origin, "health");
                    var mavlink = Read(origin, "api/v1/mavlink/health");
                    return new[] { health, mavlink };
                });
                var json = new JavaScriptSerializer();
                var h = json.Deserialize<Dictionary<string, object>>(result[0]);
                var m = json.Deserialize<Dictionary<string, object>>(result[1]);
                AeroHelmWorkspaceSettings.Save("cloud-url", origin.GetLeftPart(UriPartial.Authority));
                output.Text =
                    "Cloud: " + Convert.ToString(h["status"]) + Environment.NewLine +
                    "Mode: " + Convert.ToString(h["mode"]) + Environment.NewLine +
                    "Aircraft: " + Convert.ToString(m["vehicle_id"]) + Environment.NewLine +
                    "Pi device connected: " + Convert.ToString(m["device_online"]) + Environment.NewLine +
                    "Fresh MAVLink: " + Convert.ToString(m["fresh"]) + Environment.NewLine +
                    "Total frames: " + Convert.ToString(m["frames_received"]) + Environment.NewLine +
                    "GCS viewers: " + Convert.ToString(m["viewers"]) + Environment.NewLine +
                    "UTC checked: " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture) +
                    Environment.NewLine + Environment.NewLine +
                    "Diagnostics only. Fresh telemetry is not proof of safe flight or command authority.";
            }
            catch (Exception ex)
            {
                output.Text = "Link check failed: " + ex.Message +
                    "\nCheck Tailscale, cloud API, hostname and TLS. No certificate checks were disabled.";
            }
            finally { check.Enabled = true; }
        }
    }
}
