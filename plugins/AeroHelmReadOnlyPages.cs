// AeroHelm Windows read-only module pages. Native Mission Planner remains the
// vehicle control root; this file contains no MAVLink command serialization.
// Copyright AeroHelm contributors; GPL-3.0-or-later.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    // A standalone touch-friendly view for Fleet, Security, and Analytics.
    // It consumes EXISTING JWT-protected endpoints, not new control endpoints.
    internal sealed class AeroHelmReadOnlyPage : Form
    {
        private readonly string section;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly DataGridView table = new DataGridView();
        private readonly TextBox detail = new TextBox();
        private readonly TextBox vehicleId = new TextBox();
        private readonly ComboBox availableAircraft = new ComboBox();
        private readonly Label state = new Label();
        private readonly List<Button> actions = new List<Button>();
        private bool busy;

        private static readonly Drawing.Color Background = Drawing.Color.FromArgb(8,8,8);
        private static readonly Drawing.Color Surface = Drawing.Color.FromArgb(34,34,34);
        private static readonly Drawing.Color Ink = Drawing.Color.FromArgb(244,244,244);
        private static readonly Drawing.Color Muted = Drawing.Color.FromArgb(180,180,180);

        public AeroHelmReadOnlyPage(string module)
        {
            section = (module ?? "").ToUpperInvariant();
            if (section != "FLEET" && section != "SECURITY" && section != "ANALYTICS")
                throw new ArgumentException("Unsupported read-only module.", "module");

            Text = "AeroHelm — " + section;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Drawing.Size(700,540);
            Width = 1020;
            Height = 720;
            BackColor = Background;
            ForeColor = Ink;
            Font = new Drawing.Font("Segoe UI", 11f);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;

            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
                BackColor = Background, Padding = new Padding(18)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,76));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,58));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,148));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,35));
            Controls.Add(layout);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,75));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            header.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            header.Controls.Add(new Label {
                Text = section, Dock = DockStyle.Fill,
                Font = new Drawing.Font("Segoe UI Semibold",22f,Drawing.FontStyle.Bold),
                TextAlign = Drawing.ContentAlignment.MiddleLeft, ForeColor = Ink
            },0,0);
            header.Controls.Add(new Label {
                Text = "Authenticated cloud observations  /  Read-only  /  Values are not flight authorizations",
                Dock = DockStyle.Fill, ForeColor = Muted,
                Font = new Drawing.Font("Segoe UI",10f),
                TextAlign = Drawing.ContentAlignment.MiddleLeft
            },0,1);
            var home = new Button {
                Text = "← HOME", Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink,
                UseVisualStyleBackColor = false,
                Font = new Drawing.Font("Segoe UI Semibold",12f,Drawing.FontStyle.Bold)
            };
            home.FlatAppearance.BorderSize = 0;
            home.Click += (s,e) => Close();
            header.Controls.Add(home,1,0);
            header.SetRowSpan(home,2);
            layout.Controls.Add(header,0,0);

            var bar = new FlowLayoutPanel {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false, AutoScroll = true, Padding = new Padding(0,5,0,0)
            };
            layout.Controls.Add(bar,0,1);
            if (section == "ANALYTICS" || section == "FLEET")
            {
                bar.Controls.Add(new Label {
                    Text = "Aircraft ID", Height = 42, Width = 84,
                    TextAlign = Drawing.ContentAlignment.MiddleLeft, ForeColor = Muted
                });
                vehicleId.Width = 145; vehicleId.Height = 32;
                vehicleId.BackColor = Surface; vehicleId.ForeColor = Ink;
                vehicleId.BorderStyle = BorderStyle.FixedSingle;
                vehicleId.AccessibleName = "Aircraft identifier for view-only lookup";
                bar.Controls.Add(vehicleId);
            }

            if (section == "FLEET")
            {
                ActionButton(bar,"Refresh fleet", "fleet");
                ActionButton(bar,"Aircraft alerts", "alerts");
                SetColumns("Aircraft", "Link", "Sample age (s)", "Mode",
                           "Armed", "Battery (%)", "GNSS sats", "Risk flags");
            }
            else if (section == "SECURITY")
            {
                ActionButton(bar,"Security posture", "posture");
                ActionButton(bar,"Recent audit", "audit");
                SetColumns("Setting", "Reported value");
            }
            else
            {
                // Analytics cannot assume a hard-coded aircraft ID. Discover
                // actual registered vehicles from the existing authenticated
                // FLEET API, then require a choice if more than one is present.
                availableAircraft.DropDownStyle = ComboBoxStyle.DropDownList;
                availableAircraft.Width = 160;
                availableAircraft.Height = 35;
                availableAircraft.BackColor = Surface;
                availableAircraft.ForeColor = Ink;
                availableAircraft.AccessibleName = "Select an authenticated aircraft";
                availableAircraft.SelectedIndexChanged += (s,e) => {
                    if(availableAircraft.SelectedItem != null)
                        vehicleId.Text = Convert.ToString(availableAircraft.SelectedItem,
                            CultureInfo.InvariantCulture);
                };
                bar.Controls.Add(availableAircraft);
                ActionButton(bar,"Discover aircraft","discover");
                ActionButton(bar,"Flight report", "report");
                ActionButton(bar,"Recent telemetry", "history");
                ActionButton(bar,"Offline archive", "archive");
                SetColumns("Metric", "Reported value");
            }
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0,5,0,4) };
            table.Dock = DockStyle.Fill;
            table.BackgroundColor = Drawing.Color.FromArgb(23,23,23);
            table.GridColor = Drawing.Color.FromArgb(58,58,58);
            table.BorderStyle = BorderStyle.None;
            table.EnableHeadersVisualStyles = false;
            table.ColumnHeadersDefaultCellStyle.BackColor = Drawing.Color.FromArgb(43,43,43);
            table.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
            table.ColumnHeadersDefaultCellStyle.Font =
                new Drawing.Font("Segoe UI Semibold",10f,Drawing.FontStyle.Bold);
            table.DefaultCellStyle.BackColor = Surface;
            table.DefaultCellStyle.ForeColor = Ink;
            table.DefaultCellStyle.SelectionBackColor = Drawing.Color.FromArgb(76,76,76);
            table.DefaultCellStyle.SelectionForeColor = Ink;
            table.AlternatingRowsDefaultCellStyle.BackColor = Drawing.Color.FromArgb(29,29,29);
            table.AlternatingRowsDefaultCellStyle.ForeColor = Ink;
            table.RowHeadersVisible = false;
            table.ColumnHeadersHeight = 42;
            table.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            table.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            table.RowTemplate.Height = 38;
            table.AllowUserToAddRows = false;
            table.AllowUserToDeleteRows = false;
            table.AllowUserToResizeRows = false;
            table.ReadOnly = true;
            table.MultiSelect = false;
            table.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            table.CellClick += (s,e) => {
                if (section == "FLEET" && e.RowIndex >= 0 &&
                    table.Columns.Count == 8)
                {
                    var cell = table.Rows[e.RowIndex].Cells[0].Value;
                    if (cell != null) vehicleId.Text = Convert.ToString(cell,CultureInfo.InvariantCulture);
                }
            };
            body.Controls.Add(table);
            layout.Controls.Add(body,0,2);

            detail.Multiline = true;
            detail.ScrollBars = ScrollBars.Vertical;
            detail.ReadOnly = true;
            detail.Dock = DockStyle.Fill;
            detail.BackColor = Drawing.Color.FromArgb(22,22,22);
            detail.ForeColor = Muted;
            detail.BorderStyle = BorderStyle.None;
            detail.Font = new Drawing.Font("Consolas",10f);
            detail.Text = "No authenticated cloud data has been requested.";
            layout.Controls.Add(detail,0,3);

            state.Dock = DockStyle.Fill;
            state.TextAlign = Drawing.ContentAlignment.MiddleLeft;
            state.ForeColor = Muted;
            state.Text = "UNKNOWN  |  Press Refresh; never interpret an absent sample as a safe aircraft.";
            layout.Controls.Add(state,0,4);

            KeyDown += (s,e) => {
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            };
            Shown += async (s,e) =>
            {
                if (section == "ANALYTICS")
                {
                    await LoadAsync("discover");
                    // Only one registered aircraft can be selected without
                    // operator ambiguity; never invent or assume an ID.
                    if (!IsDisposed && !Disposing && availableAircraft.Items.Count == 1)
                        await LoadAsync("report");
                }
                else
                    await LoadAsync(section == "FLEET" ? "fleet" : "posture");
            };
        }

        private void ActionButton(FlowLayoutPanel parent, string caption, string operation)
        {
            var button = new Button {
                Text = caption, Width = Math.Max(144,caption.Length*8+24), Height = 40,
                FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink,
                Margin = new Padding(3,0,8,0), UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += async (s,e) => await LoadAsync(operation);
            parent.Controls.Add(button);
            actions.Add(button);
        }

        private void SetColumns(params string[] captions)
        {
            table.Columns.Clear();
            foreach (var caption in captions)
                table.Columns.Add("c"+table.Columns.Count,caption);
        }

        private static Dictionary<string,object> AsObject(object o)
        {
            return o as Dictionary<string,object> ?? new Dictionary<string,object>();
        }

        private static object Value(Dictionary<string,object> source,string key)
        {
            object v;
            return source != null && source.TryGetValue(key,out v) ? v : null;
        }

        private static object[] Array(object value)
        {
            return value as object[] ?? new object[0];
        }

        private static string Display(object value)
        {
            if (value == null) return "UNKNOWN";
            if (value is bool) return ((bool)value) ? "YES" : "NO";
            var list = value as object[];
            if (list != null) return list.Length == 0 ? "None reported" :
                string.Join(", ",System.Array.ConvertAll(list,Display));
            return Convert.ToString(value,CultureInfo.InvariantCulture) ?? "UNKNOWN";
        }

        private string CheckedAircraftId()
        {
            var id = vehicleId.Text.Trim();
            if (!Regex.IsMatch(id,@"\A[A-Za-z0-9_-]{3,64}\z",
                    RegexOptions.CultureInvariant))
                throw new ArgumentException("Enter/select a valid aircraft ID (3–64 letters, digits, _ or -).");
            return Uri.EscapeDataString(id);
        }

        private async Task LoadAsync(string operation)
        {
            if (busy || IsDisposed || Disposing) return;
            string route;
            try
            {
                switch (operation)
                {
                    case "fleet": route = "api/v2/fleet"; break;
                    case "discover": route = "api/v2/fleet"; break;
                    case "alerts": route = "api/v2/vehicles/" + CheckedAircraftId() + "/alerts"; break;
                    case "posture": route = "api/v2/security/posture"; break;
                    case "audit": route = "api/v2/security/audit?limit=75"; break;
                    case "report": route = "api/v2/vehicles/" + CheckedAircraftId() + "/report"; break;
                    case "history": route = "api/v2/vehicles/" + CheckedAircraftId() + "/history?limit=100"; break;
                    case "archive": route = "api/v2/vehicles/" + CheckedAircraftId() + "/archive?limit=100"; break;
                    default: throw new InvalidOperationException("Unsupported read-only action.");
                }
            }
            catch (Exception ex)
            {
                table.Rows.Clear();
                state.Text = "INPUT REQUIRED / NO VERIFIED DATA";
                detail.Text = ex.Message;
                return;
            }

            busy = true;
            foreach (var button in actions) button.Enabled = false;
            // Clear prior observations before new requests. Disconnected data must
            // not remain visible and appear to be the latest aircraft state.
            table.Rows.Clear();
            if (operation == "discover")
            {
                availableAircraft.Items.Clear();
                vehicleId.Clear();
            }
            detail.Text = "Requesting authenticated " + operation + " view...";
            state.Text = "CHECKING  |  No link/safety assumption";
            try
            {
                var response = await Task.Run(() => OperationsApi.Request("GET",route));
                if (IsDisposed || Disposing) return;
                var parsed = AsObject(json.DeserializeObject(response));
                Render(operation,parsed);
                state.Text = "OBSERVED  " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'",
                    CultureInfo.InvariantCulture) +
                    "  |  Read-only cloud snapshot; NOT an aircraft safety clearance.";
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                table.Rows.Clear();
                state.Text = "UNAVAILABLE / NOT VERIFIED";
                var web = ex as System.Net.WebException;
                var http = web == null ? null : web.Response as System.Net.HttpWebResponse;
                detail.Text = http == null ? "Unable to retrieve " + operation + ": " + ex.Message :
                    "Cloud rejected the request (HTTP " + (int)http.StatusCode +
                    "). Sign in again using CLOUD if your token expired.";
                if (http != null) http.Dispose();
                detail.Text += Environment.NewLine +
                    "Do not rely on cached telemetry or reports when the cloud is unreachable.";
            }
            finally
            {
                busy = false;
                if (!IsDisposed && !Disposing)
                    foreach (var button in actions) button.Enabled = true;
            }
        }

        private void Render(string operation, Dictionary<string,object> result)
        {
            if (operation == "discover")
            {
                SetColumns("Aircraft","Telemetry","Last sample (s)","Risk flags");
                var vehicles = Array(Value(result,"vehicles"));
                foreach (var entry in vehicles)
                {
                    var vehicle = AsObject(entry);
                    var id = Convert.ToString(Value(vehicle,"vehicle_id"),CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(id) ||
                        !Regex.IsMatch(id,@"\A[A-Za-z0-9_-]{3,64}\z",
                            RegexOptions.CultureInvariant))
                        continue;
                    availableAircraft.Items.Add(id);
                    bool fresh = Value(vehicle,"online") is bool &&
                                 (bool)Value(vehicle,"online");
                    var row = table.Rows.Add(id,fresh ? "FRESH (<5s)" : "STALE / OFF",
                        Display(Value(vehicle,"last_sample_age_s")),
                        Display(Value(vehicle,"risks")));
                    if (!fresh) table.Rows[row].DefaultCellStyle.ForeColor =
                        Drawing.Color.FromArgb(244,176,151);
                }
                if (availableAircraft.Items.Count == 1)
                    availableAircraft.SelectedIndex = 0;
                detail.Text = availableAircraft.Items.Count == 0 ?
                    "No registered aircraft are available for analytics." +
                    Environment.NewLine + "No report has been retrieved." :
                    availableAircraft.Items.Count == 1 ?
                    "One registered aircraft found. A historical report may be retrieved." :
                    "Multiple aircraft found. Choose an aircraft from the dropdown" +
                    Environment.NewLine + "before requesting a report or history.";
                detail.Text += Environment.NewLine +
                    "Fleet freshness is not aircraft flight readiness.";
            }
            else if (operation == "fleet")
            {
                SetColumns("Aircraft","Link","Sample age (s)","Mode","Armed",
                           "Battery (%)","GNSS sats","Risk flags");
                var vehicles = Array(Value(result,"vehicles"));
                int online = 0;
                foreach (var entry in vehicles)
                {
                    var v = AsObject(entry);
                    bool fresh = Value(v,"online") is bool && (bool)Value(v,"online");
                    if (fresh) online++;
                    var index = table.Rows.Add(Display(Value(v,"vehicle_id")),
                        fresh ? "FRESH (<5s)" : "STALE / OFF",
                        Display(Value(v,"last_sample_age_s")),Display(Value(v,"mode")),
                        Display(Value(v,"armed")),Display(Value(v,"battery_pct")),
                        Display(Value(v,"satellites")),Display(Value(v,"risks")));
                    if (!fresh)
                        table.Rows[index].DefaultCellStyle.ForeColor =
                            Drawing.Color.FromArgb(244,176,151);
                }
                detail.Text = "Aircraft observed: " + vehicles.Length +
                    Environment.NewLine + "Backend-reported fresh: " + online +
                    Environment.NewLine + "Physical flight control authorized: NO" +
                    Environment.NewLine + "Fleet freshness comes from the server clock and sample age." +
                    Environment.NewLine + "Select an aircraft row to inspect its advisory risk flags.";
            }
            else if (operation == "alerts")
            {
                SetColumns("Aircraft","Advisory risk flag");
                foreach (var flag in Array(Value(result,"flags")))
                    table.Rows.Add(Display(Value(result,"vehicle_id")),Display(flag));
                detail.Text = "Advisory flags for " + Display(Value(result,"vehicle_id")) +
                    Environment.NewLine + "Evaluated: " + Display(Value(result,"evaluated_at")) +
                    Environment.NewLine + "Empty flags DO NOT imply safety. Missing telemetry is never assumed healthy.";
            }
            else if (operation == "posture")
            {
                SetColumns("Security property","Cloud-reported value");
                foreach (var entry in result)
                    table.Rows.Add(entry.Key.Replace('_',' '),Display(entry.Value));
                detail.Text = "Security posture reflects the CURRENT cloud process." +
                    Environment.NewLine + "A JWT/Tailscale session is not flight-control authority." +
                    Environment.NewLine + "RAM-only audit is not a durable tamper-proof record." +
                    Environment.NewLine + "Kerberos, multi-device identity and MAVLink signing are not certified here.";
            }
            else if (operation == "audit")
            {
                SetColumns("UTC","Event","Aircraft","Actor","Detail");
                foreach (var entry in Array(Value(result,"entries")))
                {
                    var e = AsObject(entry);
                    table.Rows.Add(Display(Value(e,"at")),Display(Value(e,"kind")),
                        Display(Value(e,"vehicle")),Display(Value(e,"actor")),
                        Display(Value(e,"detail")));
                }
                detail.Text = "Audit chain verified in current process: " +
                    Display(Value(result,"current_chain_verified")) +
                    Environment.NewLine + "Retention: " + Display(Value(result,"retention")) +
                    Environment.NewLine + Display(Value(result,"warning")) +
                    Environment.NewLine + "A restarted server loses its RAM-only audit history.";
            }
            else if (operation == "report")
            {
                SetColumns("Report metric","Observed value");
                foreach (var entry in result)
                    table.Rows.Add(entry.Key.Replace('_',' '),Display(entry.Value));
                detail.Text = "Historical data only: " + Display(Value(result,"vehicle_id")) +
                    Environment.NewLine + "Source: " + Display(Value(result,"source")) +
                    Environment.NewLine + "This is NOT a flightworthiness or airspace clearance report." +
                    Environment.NewLine + "Missing GPS samples may shorten apparent distance.";
            }
            else if (operation == "history" || operation == "archive")
            {
                SetColumns("UTC sample","Latitude","Longitude","Altitude (m)",
                           "Battery (%)","Mode","Source");
                var samples = Array(Value(result,"samples"));
                foreach (var entry in samples)
                {
                    var s = AsObject(entry);
                    table.Rows.Add(Display(Value(s,"timestamp")),
                        Display(Value(s,"latitude")),Display(Value(s,"longitude")),
                        Display(Value(s,"altitude_m")),Display(Value(s,"battery_pct")),
                        Display(Value(s,"mode")),Display(Value(s,"source")));
                }
                detail.Text = (operation == "archive" ? "OFFLINE RECOVERED HISTORY" :
                               "RECENT LIVE HISTORY (bounded, not streamed)") +
                    Environment.NewLine + "Aircraft: " + Display(Value(result,"vehicle_id")) +
                    Environment.NewLine + "Returned samples: " + samples.Length +
                    Environment.NewLine + "Storage: " + Display(Value(result,"storage")) +
                    Environment.NewLine + "Historic samples must NEVER be treated as current GNSS or battery state.";
            }
        }
    }
}
