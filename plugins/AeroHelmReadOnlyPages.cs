// AeroHelm Windows read-only module pages. Native Mission Planner remains the
// vehicle control root; this file contains no MAVLink command serialization.
// Copyright AeroHelm contributors; GPL-3.0-or-later.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
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
            MinimumSize = new Drawing.Size(720,580);
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
            // Multi-row actions are visible on touch screens instead of
            // disappearing into a hard-to-use horizontal scrollbar.
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,
                section == "ANALYTICS" ? 142 : 112));
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
                WrapContents = true, AutoScroll = true, Padding = new Padding(0,5,0,0),
                AccessibleName = section + " read-only cloud actions"
            };
            layout.Controls.Add(bar,0,1);
            if (section == "ANALYTICS" || section == "FLEET" || section == "SECURITY")
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

            // Research availability is a server capability, not evidence of
            // an installed physics twin, Kerberos realm or flight authorization.
            ActionButton(bar,"Cloud capabilities", "capabilities");
            if (section == "FLEET")
            {
                ActionButton(bar,"Refresh fleet", "fleet");
                ActionButton(bar,"Cloud metrics", "cloudMetrics");
                ActionButton(bar,"Aircraft alerts", "alerts");
                ActionButton(bar,"Shadow fleet", "researchFleet");
                var scheduler=new Button {
                    Text="Fleet scheduler lab",Width=190,Height=40,
                    FlatStyle=FlatStyle.Flat,BackColor=Surface,ForeColor=Ink,
                    Margin=new Padding(3,0,8,0),UseVisualStyleBackColor=false
                };
                scheduler.FlatAppearance.BorderSize=0;
                scheduler.Click+=(s,e)=> {
                    using(var window=new AeroHelmFleetLabWindow())
                        window.ShowDialog(this);
                };
                bar.Controls.Add(scheduler);
                SetColumns("Aircraft", "Link", "Sample age (s)", "Mode",
                           "Armed", "Battery (%)", "GNSS", "Risk flags");
            }
            else if (section == "SECURITY")
            {
                ActionButton(bar,"Security posture", "posture");
                ActionButton(bar,"Kerberos readiness", "kerberos");
                ActionButton(bar,"Recent audit", "audit");
                ActionButton(bar,"CyberShield", "cyberShadow");
                ActionButton(bar,"Evidence integrity", "integrity");
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
                ActionButton(bar,"Cloud portfolio","cloudMetrics");
                ActionButton(bar,"Flight report", "report");
                ActionButton(bar,"Recent telemetry", "history");
                ActionButton(bar,"Offline archive", "archive");
                ActionButton(bar,"Digital shadow", "digitalShadow");
                ActionButton(bar,"Evidence replay", "evidence");
                ActionButton(bar,"Camera events", "visionEvents");
                var twinLab=new Button {
                    Text="Twin calibration lab",Width=200,Height=40,
                    FlatStyle=FlatStyle.Flat,BackColor=Surface,ForeColor=Ink,
                    Margin=new Padding(3,0,8,0),UseVisualStyleBackColor=false
                };
                twinLab.FlatAppearance.BorderSize=0;
                twinLab.Click+=(s,e)=> {
                    using(var window=new AeroHelmTwinLabWindow())
                        window.ShowDialog(this);
                };
                bar.Controls.Add(twinLab);
                var physicsLab=new Button {
                    Text="Six-DOF physics lab",Width=186,Height=40,
                    FlatStyle=FlatStyle.Flat,BackColor=Surface,ForeColor=Ink,
                    Margin=new Padding(3,0,8,0),UseVisualStyleBackColor=false
                };
                physicsLab.FlatAppearance.BorderSize=0;
                physicsLab.Click+=(s,e)=> {
                    using(var window=new AeroHelmPhysicsLabWindow())
                        window.ShowDialog(this);
                };
                bar.Controls.Add(physicsLab);
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
            table.RowTemplate.Height = 44;
            table.AccessibleName = section + " read-only cloud evidence table";
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

            var configuredAircraft =
                Environment.GetEnvironmentVariable("AEROHELM_VEHICLE_ID");
            if (!string.IsNullOrEmpty(configuredAircraft) &&
                Regex.IsMatch(configuredAircraft,@"\A[A-Za-z0-9_-]{3,64}\z",
                    RegexOptions.CultureInvariant))
                vehicleId.Text = configuredAircraft; // Identifier only; not proof of live link.
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
                Text = caption, Width = Math.Max(144,caption.Length*8+24), Height = 46,
                FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink,
                Margin = new Padding(3,0,8,6), UseVisualStyleBackColor = false,
                AccessibleName = caption + " (read-only cloud request)"
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


        // The deployed M1 Oracle returns a JSON array from GET /api/v1/vehicles,
        // while FLEET/ANALYTICS v2 expects {"vehicles":[...]}. Convert only the
        // registry view. Unknown risk/security/history data stays unavailable.
        private static string LegacyFleetSnapshot(string jsonArray)
        {
            var decoder = new JavaScriptSerializer();
            var rows = decoder.DeserializeObject(jsonArray) as object[];
            if (rows == null)
                throw new InvalidOperationException("Unexpected M1 registry response.");
            var mapped = new List<Dictionary<string,object>>();
            foreach (var entry in rows)
            {
                var v = AsObject(entry);
                var id = Value(v,"vehicle_id") as string;
                if (string.IsNullOrWhiteSpace(id) ||
                    !Regex.IsMatch(id,@"\A[A-Za-z0-9_-]{3,64}\z",
                                   RegexOptions.CultureInvariant))
                    continue;
                DateTimeOffset timestamp;
                var rawTimestamp = Convert.ToString(Value(v,"timestamp"),
                                                     CultureInfo.InvariantCulture);
                bool validTime = DateTimeOffset.TryParse(rawTimestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out timestamp);
                double ageSeconds = validTime ?
                    Math.Max(0,(DateTimeOffset.UtcNow - timestamp).TotalSeconds) :
                    double.NaN;
                bool recent = validTime && ageSeconds < 5 &&
                    ageSeconds >= 0 && timestamp <= DateTimeOffset.UtcNow.AddSeconds(2);
                bool online = string.Equals(Convert.ToString(Value(v,"connection")),
                                            "online",StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(Convert.ToString(Value(v,"source")),
                                            "hardware",StringComparison.OrdinalIgnoreCase) &&
                              recent;
                mapped.Add(new Dictionary<string,object> {
                    {"vehicle_id",id},
                    {"online",online},
                    {"last_sample_age_s",validTime ? (object)Math.Round(ageSeconds,1) : null},
                    {"mode",Value(v,"mode")},
                    {"armed",Value(v,"armed")},
                    {"battery_pct",Value(v,"battery_pct")},
                    {"satellites",Value(v,"satellites")},
                    {"risks",new object[]{"M1: risk/security data unavailable"}}
                });
            }
            return decoder.Serialize(new Dictionary<string,object> {
                {"vehicles",mapped}
            });
        }

        private async Task LoadAsync(string operation)
        {
            if (busy || IsDisposed || Disposing) return;
            if (operation == "kerberos")
            {
                // This is an explicit integration gate, NOT a simulated login,
                // fake Kerberos ticket or claim of installed KDC protection.
                SetColumns("Security layer", "Deployment state");
                table.Rows.Add("Tailscale private HTTPS", "Current transport boundary; verify tailnet ACLs");
                table.Rows.Add("Operator JWT", "Current application identity; short-lived session");
                table.Rows.Add("Kerberos KDC/realm", "NOT PROVISIONED");
                table.Rows.Add("SPNEGO service principal and keytab", "NOT PROVISIONED");
                table.Rows.Add("Trusted gateway / actor-role mapping", "NOT PROVISIONED");
                table.Rows.Add("Replay/clock / key rotation", "NOT VERIFIED");
                table.Rows.Add("MAVLink2 FC signing", "NOT VERIFIED");
                detail.Text = "KERBEROS NOT ACTIVE — no realm, trusted gateway or keytab has been configured." +
                    Environment.NewLine +
                    "The GCS uses operator JWT over private Tailscale HTTPS; these are not Kerberos tickets." +
                    Environment.NewLine +
                    "KDC, SPNEGO authentication, explicit role mapping, replay protection, rotation and" +
                    " tested fail-closed denial are required before enabling a Kerberos-backed gateway." +
                    Environment.NewLine +
                    "No cloud flight-control authority is provided.";
                state.Text = "NOT CONFIGURED  |  Architectural readiness only; no token or ticket created.";
                state.ForeColor = Drawing.Color.FromArgb(244,210,161);
                try
                {
                    // Configuration discovery is authenticated; it cannot
                    // authenticate the Windows client or prove the KDC works.
                    var payload=await Task.Run(()=>OperationsApi.Request(
                        "GET","api/v1/security/capabilities"));
                    if(IsDisposed||Disposing)return;
                    var fields=json.Deserialize<Dictionary<string,object>>(payload);
                    object requested,ready,kdc;
                    bool configured=fields.TryGetValue("kerberos_sso_requested",out requested) &&
                        requested is bool && (bool)requested;
                    bool verifier=fields.TryGetValue("kerberos_assertion_verifier_ready",out ready) &&
                        ready is bool && (bool)ready;
                    bool tested=fields.TryGetValue("kerberos_kdc_and_spnego_verified",out kdc) &&
                        kdc is bool && (bool)kdc;
                    table.Rows[2].Cells[1].Value=tested ?
                        "EXTERNALLY VERIFIED — CHECK OPERATOR POLICY" :
                        "NOT VERIFIED";
                    table.Rows[4].Cells[1].Value=verifier ?
                        "ONE-TIME ASSERTION VERIFIER CONFIGURED" : "NOT PROVISIONED";
                    state.Text=configured && verifier ?
                        "GATEWAY ASSERTION VERIFIER CONFIGURED; KDC/SSO ACCEPTANCE STILL REQUIRED" :
                        "KERBEROS NOT ACTIVE — PASSWORD JWT LOGIN AVAILABLE";
                    detail.Text+="\r\nCloud reports: assertion verifier "+
                        (verifier?"configured":"not configured")+
                        ". This is not proof of a reachable KDC, valid keytab or SPNEGO login.";
                }
                catch(WebException)
                {
                    if(!IsDisposed&&!Disposing)
                        detail.Text+="\r\nCloud M1 security capability endpoint unavailable; "+
                            "server deployment not confirmed.";
                }
                catch(InvalidOperationException)
                {
                    if(!IsDisposed&&!Disposing)
                        detail.Text+="\r\nOperator session unavailable for security capabilities.";
                }
                return;
            }
            string route;
            try
            {
                switch (operation)
                {
                    case "fleet": route = "api/v2/fleet"; break;
                    case "cloudMetrics": route = "api/v1/fleet/metrics"; break;
                    case "capabilities": route = "api/v3/research/capabilities"; break;
                    case "researchFleet": route = "api/v3/research/fleet"; break;
                    case "integrity": route = "api/v3/research/evidence-integrity"; break;
                    case "digitalShadow": route = "api/v3/vehicles/" + CheckedAircraftId() + "/digital-shadow"; break;
                    case "cyberShadow": route = "api/v3/vehicles/" + CheckedAircraftId() + "/cyber-events"; break;
                    case "evidence": route = "api/v3/vehicles/" + CheckedAircraftId() + "/evidence?limit=100"; break;
                    case "visionEvents": route = "api/v3/vehicles/" + CheckedAircraftId() + "/vision-events"; break;
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
                bool usedM1Registry = false;
                bool usedCloudControlPlane = false;
                var response = await Task.Run(() => {
                    try { return OperationsApi.Request("GET",route); }
                    catch (WebException ex)
                    {
                        var http = ex.Response as HttpWebResponse;
                        if ((operation != "fleet" && operation != "discover") ||
                            http == null || http.StatusCode != HttpStatusCode.NotFound)
                            throw;
                        http.Dispose();
                        // New independent cloud R1/R2 fleet observations.
                        // Never issue commands to discover fleet identity.
                        try
                        {
                            var cloud = OperationsApi.Request(
                                "GET","api/v1/fleet/status");
                            var mapped = CloudV1FleetSnapshot(cloud);
                            usedCloudControlPlane = true;
                            return mapped;
                        }
                        catch (WebException cloudFailure)
                        {
                            var second = cloudFailure.Response as HttpWebResponse;
                            if (second == null ||
                                second.StatusCode != HttpStatusCode.NotFound)
                                throw;
                            second.Dispose();
                        }
                        var legacy = OperationsApi.Request("GET","api/v1/vehicles");
                        var converted = LegacyFleetSnapshot(legacy);
                        usedM1Registry = true;
                        return converted;
                    }
                });
                if (IsDisposed || Disposing) return;
                var parsed = AsObject(json.DeserializeObject(response));
                if (operation == "capabilities" ||
                    operation == "researchFleet" || operation == "integrity" ||
                    operation == "digitalShadow" || operation == "cyberShadow" ||
                    operation == "evidence" || operation == "visionEvents")
                    RenderResearch(operation,parsed);
                else
                    Render(operation,parsed);
                state.Text = "OBSERVED  " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'",
                    CultureInfo.InvariantCulture) +
                    (usedM1Registry ?
                        "  |  LEGACY M1 REGISTRY; RISK/HISTORY NOT AVAILABLE" :
                     usedCloudControlPlane ?
                        "  |  CLOUD R2 DEVICE-REPORTED; NOT FLIGHT-CLEARED" :
                        "  |  Read-only cloud snapshot; NOT an aircraft safety clearance.");
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                table.Rows.Clear();
                state.Text = "UNAVAILABLE / NOT VERIFIED";
                var web = ex as System.Net.WebException;
                var http = web == null ? null : web.Response as System.Net.HttpWebResponse;
                if (http == null)
                    detail.Text = "Unable to retrieve " + operation + ": " + ex.Message;
                else if (http.StatusCode == System.Net.HttpStatusCode.NotFound)
                    detail.Text = "Read-only cloud capability/aircraft not found (HTTP 404)." +
                        Environment.NewLine +
                        (operation == "capabilities" || operation == "researchFleet" ||
                         operation == "integrity" || operation == "digitalShadow" ||
                         operation == "cyberShadow" || operation == "visionEvents" ||
                         operation == "evidence" ?
                         "Research V3 is not deployed or enabled on this Oracle server. " +
                         "A digital shadow/twin lab cannot become operational from GCS code alone." :
                         "This action requires AeroHelm v2 endpoints not provided by M1.") +
                        Environment.NewLine +
                        "Kerberos requires a provisioned KDC, SPNEGO gateway and identity policy. " +
                        "No Kerberos authentication is active in this GCS.";
                else if (http.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                         http.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    detail.Text = "Cloud authentication/permission failed (HTTP " +
                        (int)http.StatusCode + "). Sign in again using CLOUD.";
                else
                    detail.Text = "Cloud rejected the request (HTTP " +
                        (int)http.StatusCode + "). Verify backend availability.";
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


        // Research V3 is an experimental observational service. Do not
        // present a digital shadow as a certified physical dynamics twin.
        private void RenderResearch(string operation, Dictionary<string,object> result)
        {
            SetColumns("Attribute","Reported value");
            foreach (var entry in result)
            {
                if (entry.Value is object[] || entry.Value is Dictionary<string,object>)
                    table.Rows.Add(entry.Key,"Open structured evidence below");
                else
                    table.Rows.Add(entry.Key,Display(entry.Value));
            }
            detail.Text = "RESEARCH EXPERIMENTAL: " + operation +
                Environment.NewLine + "Kerberos integration: NOT PROVISIONED (requires KDC/gateway)." +
                Environment.NewLine + "No physical flight-control authority." +
                Environment.NewLine + "Missing or stale samples are not proof of safety." +
                Environment.NewLine + "A short-horizon constant-velocity digital shadow is NOT" +
                " a validated aircraft physics model." +
                Environment.NewLine + Environment.NewLine + json.Serialize(result);
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
                           "Battery (%)","GNSS","Risk flags");
                var vehicles = Array(Value(result,"vehicles"));
                int online = 0;
                foreach (var entry in vehicles)
                {
                    var v = AsObject(entry);
                    bool fresh = Value(v,"online") is bool && (bool)Value(v,"online");
                    if (fresh) online++;
                    var kind = Convert.ToString(Value(v,"kind"),
                        CultureInfo.InvariantCulture);
                    var link = !fresh ? "STALE / OFF" :
                        string.Equals(kind,"virtual",StringComparison.OrdinalIgnoreCase) ?
                            "VIRTUAL / SIM" : "DEVICE REPORTED";
                    var index = table.Rows.Add(Display(Value(v,"vehicle_id")),
                        link,
                        Display(Value(v,"last_sample_age_s")),Display(Value(v,"mode")),
                        Display(Value(v,"armed")),Display(Value(v,"battery_pct")),
                        Display(Value(v,"satellites")),Display(Value(v,"risks")));
                    if (!fresh)
                        table.Rows[index].DefaultCellStyle.ForeColor =
                            Drawing.Color.FromArgb(244,176,151);
                }
                detail.Text = "Aircraft observed: " + vehicles.Length +
                    Environment.NewLine + "Backend-reported fresh: " + online +
                    Environment.NewLine + "Hardware telemetry is DEVICE REPORTED, not independently attested." +
                    Environment.NewLine + "Virtual vehicles are simulations, not physical flights." +
                    Environment.NewLine + "Physical flight control authorized: NO" +
                    Environment.NewLine + "Fleet freshness comes from the server clock and sample age." +
                    Environment.NewLine + "Select an aircraft row to inspect its advisory risk flags.";
            }
            else if (operation == "cloudMetrics")
            {
                // Only bounded server-aggregated metrics; no portfolio joins
                // or historical telemetry processing in the Windows GCS.
                SetColumns("Cloud fleet metric", "Server-reported value");
                foreach (var field in new [] {
                    "vehicles", "virtual_vehicles", "hardware_observation_only",
                    "fresh_telemetry_sources", "queued_virtual_jobs"
                })
                    table.Rows.Add(field.Replace('_',' '),
                        Display(Value(result,field)));
                foreach (var pair in AsObject(Value(result,"missions_by_state")))
                    table.Rows.Add("mission " + pair.Key, Display(pair.Value));
                detail.Text = "CLOUD AGGREGATION / DEVICE-REPORTED EVIDENCE ONLY" +
                    Environment.NewLine +
                    "The cloud computes these totals; the GCS only displays results." +
                    Environment.NewLine +
                    "Missing telemetry, audit or mission data does not imply safety." +
                    Environment.NewLine +
                    "No aircraft commands or mission starts are issued.";
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
