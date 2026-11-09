// AeroHelm INTEL: fleet, passive preflight, configuration drift, history, audit.
// Read-only cloud operations ONLY. No remote flight or parameter commands.
// Copyright AeroHelm contributors. GPL-3.0-or-later.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmIntelWindow : Form
    {
        private readonly JavaScriptSerializer json=new JavaScriptSerializer();
        private readonly TextBox vehicle=new TextBox();
        private readonly TextBox report=new TextBox();
        private readonly NumericUpDown altitude=new NumericUpDown();
        private readonly NumericUpDown speed=new NumericUpDown();
        private readonly TextBox points=new TextBox();
        private readonly TextBox fence=new TextBox();
        private readonly TextBox targets=new TextBox();
        private readonly Label banner=new Label();
        private string lastMissionHash;
        private string lastMissionPlan;
        private bool running;

        public AeroHelmIntelWindow(string initialTab = "FLEET")
        {
            Text="VIHANGA — Fleet Intelligence";
            StartPosition=FormStartPosition.CenterParent;
            Width=920;Height=740;MinimumSize=new Drawing.Size(560,440);
            var header=new FlowLayoutPanel{Dock=DockStyle.Top,Height=76,Padding=new Padding(8),WrapContents=true,AutoScroll=true};
            header.Controls.Add(new Label{Text="Aircraft:",Width=65,
                TextAlign=Drawing.ContentAlignment.MiddleRight,Height=26});
            vehicle.Text="";vehicle.Width=160;vehicle.AccessibleName="Aircraft identifier";header.Controls.Add(vehicle);
            vehicle.TextChanged += (s,e) => InvalidateMissionValidation();
            banner.Text="ANALYSIS ONLY — no mission upload, flight command, calibration or parameter write.";
            banner.AutoSize=true;banner.ForeColor=Drawing.Color.DarkRed;header.Controls.Add(banner);
            var tabs=new TabControl{Dock=DockStyle.Fill};
            var fleet=new TabPage("FLEET");
            var mission=new TabPage("PREFLIGHT");
            var param=new TabPage("PARAMETERS");
            var hist=new TabPage("HISTORY");
            var security=new TabPage("SECURITY");
            tabs.TabPages.AddRange(new[]{fleet,mission,param,hist,security});
            // Touch launcher opens the requested domain directly rather
            // than forcing an additional tab-selection gesture.
            switch ((initialTab ?? "FLEET").ToUpperInvariant())
            {
                case "SECURITY": tabs.SelectedTab = security; break;
                case "ANALYTICS": tabs.SelectedTab = hist; break;
                case "PLAN": tabs.SelectedTab = mission; break;
                case "PARAMETERS": tabs.SelectedTab = param; break;
                default: tabs.SelectedTab = fleet; break;
            }

            report.Dock=DockStyle.Bottom;
            report.Height=240;
            report.ReadOnly=true;
            report.Multiline=true;
            report.ScrollBars=ScrollBars.Both;
            report.WordWrap=false;
            report.Font=new Drawing.Font("Consolas",10);
            report.Text="Select a module to inspect authenticated cloud state.\r\n" +
                        "Mission checks are advisory; flight authorization is ALWAYS false.";
            Controls.Add(tabs);
            Controls.Add(report);
            Controls.Add(header);

            var fleetPanel=Panel();
            fleet.Controls.Add(fleetPanel);
            fleetPanel.Controls.Add(ActionButton("Refresh fleet status",
                async()=>await Run(()=>AeroHelmBackendProfile.ReadFleet("api/v2/fleet"),
                                     "Fleet health (negotiated read-only API)")));
            fleetPanel.Controls.Add(ActionButton("Aircraft risk flags",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/vehicles/"+Id()+"/alerts"),"Aircraft alerts")));
            // R2 exposes a separate read-only, virtual-fleet contract.
            // Do not treat an R2 telemetry aggregate as flight clearance.
            fleetPanel.Controls.Add(ActionButton("Cloud fleet status (v1)",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v1/fleet/status"),"Cloud fleet status — not flight authority")));
            fleetPanel.Controls.Add(ActionButton("Cloud fleet metrics (v1)",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v1/fleet/metrics"),"Cloud fleet metrics")));
            fleetPanel.Controls.Add(new Label{Text="Fleet telemetry may be stale or simulated. Check source and age; missing data remains UNKNOWN.",
                AutoSize=true,ForeColor=Drawing.Color.DarkRed});

            var missionPanel=Panel();mission.Controls.Add(missionPanel);
            missionPanel.Controls.Add(new Label{Text="Enter mission waypoints: latitude,longitude — one line per waypoint (minimum 2)",
                Width=650,Height=24});
            points.Multiline=true;points.ScrollBars=ScrollBars.Vertical;
            points.Width=650;points.Height=85;missionPanel.Controls.Add(points);
            missionPanel.Controls.Add(ActionButton("Load SURVEY waypoint file",()=>LoadSurvey()));
            missionPanel.Controls.Add(new Label{Text="Optional local geofence polygon: latitude,longitude (3–40 vertices). No legal/terrain database is bundled.",
                Width=650,Height=28});
            fence.Multiline=true;fence.ScrollBars=ScrollBars.Vertical;
            fence.Width=650;fence.Height=75;missionPanel.Controls.Add(fence);
            var opts=new FlowLayoutPanel{Width=660,Height=42};
            altitude.Minimum=5;altitude.Maximum=120;altitude.Value=30;
            altitude.Width=65;altitude.DecimalPlaces=1;
            speed.Minimum=1;speed.Maximum=15;speed.Value=5;
            speed.Width=65;speed.DecimalPlaces=1;
            opts.Controls.Add(new Label{Text="Relative altitude (m)",AutoSize=true});
            opts.Controls.Add(altitude);
            opts.Controls.Add(new Label{Text="Nominal speed (m/s)",AutoSize=true});
            opts.Controls.Add(speed);
            missionPanel.Controls.Add(opts);
            points.TextChanged += (s,e) => InvalidateMissionValidation();
            fence.TextChanged += (s,e) => InvalidateMissionValidation();
            altitude.ValueChanged += (s,e) => InvalidateMissionValidation();
            speed.ValueChanged += (s,e) => InvalidateMissionValidation();
            missionPanel.Controls.Add(ActionButton("Validate route & compute integrity SHA-256",
                async()=>await ValidateMission()));
            missionPanel.Controls.Add(ActionButton("Start passive mission shadow",
                async()=>await CreateShadow()));
            missionPanel.Controls.Add(ActionButton("Check shadow deviation",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/missions/shadow/"+Id()),"Mission shadow")));
            missionPanel.Controls.Add(ActionButton("Stop mission shadow",
                async()=>await Run(()=>OperationsApi.Request("DELETE",
                    "api/v2/missions/shadow/"+Id()),"Stop passive shadow")));

            var paramPanel=Panel();param.Controls.Add(paramPanel);
            paramPanel.Controls.Add(new Label{Text="Compare with collected parameters (NAME=VALUE per line). Read-only.",
                Width=650,Height=24});
            targets.Width=650;targets.Height=160;targets.Multiline=true;
            targets.ScrollBars=ScrollBars.Vertical;paramPanel.Controls.Add(targets);
            paramPanel.Controls.Add(new Label {
                Text="Group by prefix: ARMING / BATT / FS / INS / RC / SERIAL. " +
                     "Names and values are compared only; cloud writes are disabled.",
                AutoSize=true
            });
            paramPanel.Controls.Add(ActionButton("Sort parameter targets locally",
                ()=>SortParameterTargets()));
            paramPanel.Controls.Add(ActionButton("Compare parameter snapshot",
                async()=>await CompareParameters()));
            var refreshFromPi = ActionButton("Request disarmed read-only parameter capture",
                async()=>await Run(()=>
                    VihangaLinkSession.UsesDedicatedRelay ?
                        VihangaLinkSession.RequestParameterSnapshot(Id()) :
                        OperationsApi.Request("POST",
                            "api/v1/vehicles/"+Id()+"/parameters/refresh"),
                    "Read-only parameter capture request"));
            // Relay and Pi have independent opt-ins; a QUEUED response is
            // not evidence that the complete parameter list was received.
            refreshFromPi.Visible = true;
            paramPanel.Controls.Add(refreshFromPi);
            paramPanel.Controls.Add(ActionButton("Inspect parameter snapshot completeness",
                async()=>await ParameterSnapshotStatus()));
            var fullTable = ActionButton("View complete verified onboard parameter list",
                async()=>await ViewFullParameters());
            fullTable.Visible = VihangaLinkSession.UsesDedicatedRelay;
            paramPanel.Controls.Add(fullTable);
            paramPanel.Controls.Add(new Label{Text="Use SETUP/local connection for supported authorized changes. This panel never writes values.",
                AutoSize=true,ForeColor=Drawing.Color.DarkRed});

            var hPanel=Panel();hist.Controls.Add(hPanel);
            hPanel.Controls.Add(ActionButton("Recent live telemetry (up to 100)",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/vehicles/"+Id()+"/history?limit=100"),"Live history")));
            hPanel.Controls.Add(ActionButton("Virtual mission registry (v1)",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v1/missions?limit=50"),"Virtual mission registry — no hardware dispatch")));
            hPanel.Controls.Add(ActionButton("Generate flight data report",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/vehicles/"+Id()+"/report"),"Flight report")));
            hPanel.Controls.Add(ActionButton("Preview GeoJSON historical flight path",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/vehicles/"+Id()+"/replay.geojson"),"Flight path GeoJSON")));
            hPanel.Controls.Add(ActionButton("Historical offline telemetry (up to 100)",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/vehicles/"+Id()+"/archive?limit=100"),"Offline journal")));
            hPanel.Controls.Add(new Label{Width=650,Height=65,
                Text="Live and historical telemetry are separate.\r\n" +
                     "Server RAM history disappears on restart; the optional Pi SQLite journal persists locally."});

            var secPanel=Panel();security.Controls.Add(secPanel);
            secPanel.Controls.Add(ActionButton("Inspect cloud flight-control readiness",
                async()=>await Run(()=>
                    VihangaLinkSession.UsesDedicatedRelay ?
                        VihangaLinkSession.ReadFlightControlStatus() :
                        "Dedicated private relay not selected.",
                    "Cloud flight-control readiness — physical dispatch disabled")));
            secPanel.Controls.Add(ActionButton("Cloud security posture",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/security/posture"),"Security posture")));
            secPanel.Controls.Add(ActionButton("Verify recent audit chain",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/security/audit?limit=100"),"Signed in-RAM audit")));
            secPanel.Controls.Add(new Label{Width=670,Height=90,
                Text="Kerberos is an optional enterprise sign-in integration, NOT part of the drone data plane.\r\n" +
                     "A verified in-memory HMAC audit chain is NOT a tamper-proof durable record.\r\n" +
                     "Full MAVLink signing and multi-device authority are not yet configured."});
            VihangaAdaptivePanel.Attach(this,fleetPanel,missionPanel,paramPanel,hPanel,secPanel);
            SizeChanged+=(s,e)=>{
                if(!IsDisposed && !Disposing)
                    report.Height=Math.Max(105,Math.Min(240,ClientSize.Height/3));
            };
        }

        private static FlowLayoutPanel Panel()
        {
            return new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,
                FlowDirection=FlowDirection.TopDown,WrapContents=false,
                Padding=new Padding(14)};
        }

        private static Button ActionButton(string label,Action action)
        {
            var button=new Button{Text=label,AutoSize=true,MinimumSize=new Drawing.Size(265,28)};
            button.Click+=(s,e)=>action();
            return button;
        }

        private string Id()
        {
            if (vehicle.InvokeRequired)
                return (string)vehicle.Invoke(new Func<string>(Id));
            if (vehicle.IsDisposed)
                throw new ObjectDisposedException("Aircraft selector");
            string ident=vehicle.Text.Trim();
            if(ident.Length<3||ident.Length>64||
               ident.Any(ch=>!((ch>='A'&&ch<='Z') ||
                               (ch>='a'&&ch<='z') ||
                               (ch>='0'&&ch<='9') || ch=='-' || ch=='_')))
                throw new InvalidOperationException("Aircraft ID must be 3–64 ASCII letters, digits, - or _.");
            return Uri.EscapeDataString(ident);
        }

        private async Task Run(Func<string> job,string title)
        {
            if(running)return;
            running=true;
            string aircraftAtRequest=vehicle.Text;
            report.Text=title+": contacting authenticated cloud...";
            try
            {
                var result=await Task.Run(job);
                if(IsDisposed || Disposing) return;
                report.Text=vehicle.Text==aircraftAtRequest ?
                    title+"\r\n"+result :
                    "Aircraft selection changed; earlier cloud response discarded.";
            }
            catch(Exception ex)
            {
                if(!IsDisposed && !Disposing)
                    report.Text=title+" failed: "+Explain(ex);
            }
            finally {running=false;}
        }

        private static string Explain(Exception ex)
        {
            var web=ex as System.Net.WebException;
            if(web!=null && web.Response is System.Net.HttpWebResponse response)
            {
                var code=(int)response.StatusCode;
                response.Dispose();
                return "Cloud returned HTTP "+code;
            }
            return ex.Message;
        }

        private static List<object> ReadCoordinates(string lines,int low,int high)
        {
            var parsed=new List<object>();
            foreach(string line in lines.Split(new[]{'\r','\n'},
                     StringSplitOptions.RemoveEmptyEntries))
            {
                var fields=line.Trim().Split(',');
                if(fields.Length!=2)throw new ArgumentException("Each waypoint must be latitude,longitude");
                double lat,lon;
                if(!double.TryParse(fields[0],NumberStyles.Float,CultureInfo.InvariantCulture,out lat) ||
                   !double.TryParse(fields[1],NumberStyles.Float,CultureInfo.InvariantCulture,out lon) ||
                   double.IsNaN(lat)||double.IsInfinity(lat)||lat < -85||lat > 85||
                   double.IsNaN(lon)||double.IsInfinity(lon)||lon < -180||lon > 180)
                    throw new ArgumentException("Invalid waypoint/geofence coordinates");
                parsed.Add(new { lat=lat,lon=lon });
            }
            if(parsed.Count<low||parsed.Count>high)
                throw new ArgumentException("Coordinates require "+low+"–"+high+" points");
            return parsed;
        }

        private void InvalidateMissionValidation()
        {
            lastMissionHash = null;
            lastMissionPlan = null;
        }

        private object BuildPlan()
        {
            var track=ReadCoordinates(points.Text,2,120);
            List<object> polygon=string.IsNullOrWhiteSpace(fence.Text) ?
                null : ReadCoordinates(fence.Text,3,40);
            return new {points=track,geofence=polygon,
                relative_altitude_m=(double)altitude.Value,
                cruise_speed_mps=(double)speed.Value,
                reserve_fraction=0.3,
                terrain_clearance_verified=false };
        }

        private async Task ValidateMission()
        {
            if(running)return;
            object plan;
            try{plan=BuildPlan();}
            catch(Exception ex){report.Text=ex.Message;return;}
            string submittedPlan = json.Serialize(plan);
            string aircraftAtValidation = vehicle.Text;
            InvalidateMissionValidation();
            running=true;
            try
            {
                var body=await Task.Run(()=>OperationsApi.Request(
                    "POST","api/v2/missions/validate",plan));
                var response=json.Deserialize<Dictionary<string,object>>(body);
                string hash=Convert.ToString(response["mission_sha256"]);
                if (string.IsNullOrWhiteSpace(hash))
                    throw new InvalidOperationException("Cloud did not return a verified mission hash.");
                if (!string.Equals(json.Serialize(BuildPlan()),submittedPlan,StringComparison.Ordinal) ||
                    !string.Equals(vehicle.Text,aircraftAtValidation,StringComparison.Ordinal))
                    throw new InvalidOperationException("Plan or aircraft changed during validation. Validate again.");
                lastMissionHash=hash;
                lastMissionPlan=submittedPlan;
                report.Text="PREFLIGHT (advisory, NEVER flight authorization)\r\n"+body;
            }
            catch(Exception ex){report.Text=Explain(ex);}
            finally{running=false;}
        }

        private async Task CreateShadow()
        {
            if(string.IsNullOrWhiteSpace(lastMissionHash))
            {report.Text="Run Preflight validation first.";return;}
            object plan;
            try{plan=BuildPlan();}
            catch(Exception ex){report.Text=ex.Message;return;}
            if (!string.Equals(lastMissionPlan,json.Serialize(plan),StringComparison.Ordinal))
            {
                report.Text="The route or operating limits changed. Revalidate before starting a passive shadow.";
                InvalidateMissionValidation();
                return;
            }
            string shadowSubject;
            try { shadowSubject=Id(); }
            catch(Exception ex) { report.Text=ex.Message; return; }
            string approvedHash=lastMissionHash;
            // The backend independently validates geometry and hash matching.
            await Run(()=>OperationsApi.Request("POST",
                 "api/v2/missions/shadow/"+shadowSubject,
                 new{plan=plan,expected_sha256=approvedHash}),
                 "Mission shadow");
        }

        private async Task ParameterSnapshotStatus()
        {
            if(running)return;
            string subject;
            try { subject=Id(); }
            catch(Exception ex) { report.Text=ex.Message; return; }
            string aircraftAtRequest=vehicle.Text;
            running=true;
            report.Text="Querying authenticated parameter cache state...";
            try
            {
                var body=await Task.Run(()=>VihangaLinkSession.UsesDedicatedRelay ?
                    VihangaLinkSession.ReadParameters(subject) :
                    OperationsApi.Request("GET","api/v1/vehicles/"+subject+"/parameters"));
                if(IsDisposed || Disposing) return;
                if(vehicle.Text!=aircraftAtRequest)
                {
                    report.Text="Aircraft selection changed; parameter response discarded.";
                    return;
                }
                var result=json.Deserialize<Dictionary<string,object>>(body);
                bool complete=result.ContainsKey("verified_complete") &&
                    Convert.ToBoolean(result["verified_complete"]);
                report.Text="Pixhawk parameter snapshot status: "+Convert.ToString(result["status"])+
                    Environment.NewLine+
                    "Received parameters: "+Convert.ToString(result["count"])+
                    " / "+Convert.ToString(result["expected"])+Environment.NewLine+
                    "Verified complete: "+complete+Environment.NewLine+
                    (VihangaLinkSession.UsesDedicatedRelay ?
                        "Oldest sample age (ms): " +
                            (result.ContainsKey("age_ms") ?
                                Convert.ToString(result["age_ms"]) : "unknown") :
                        "Captured at (UTC): " +
                            (result.ContainsKey("captured_at") ?
                                Convert.ToString(result["captured_at"]) : "unknown"))+
                    Environment.NewLine+
                    "This is a previously collected Pi snapshot, not a live parameter read. "+
                    "It cannot authorize remote configuration, mission upload or armed operation.";
            }
            catch(Exception ex)
            {
                if(!IsDisposed && !Disposing)
                    report.Text="Parameter inspection failed: "+Explain(ex);
            }
            finally{running=false;}
        }

        private async Task ViewFullParameters()
        {
            if (!VihangaLinkSession.UsesDedicatedRelay)
            {
                report.Text = "Full onboard inventory requires a dedicated authenticated Vihanga relay.";
                return;
            }
            string aircraft;
            try { aircraft = Id(); }
            catch (Exception ex) { report.Text = ex.Message; return; }
            await Run(() =>
            {
                // The relay rejects partial/stale parameter tables; no fake
                // empty values or guessed defaults are shown to the operator.
                var raw = VihangaLinkSession.ReadParameters(aircraft, true);
                var payload = json.Deserialize<Dictionary<string,object>>(raw);
                object statusValue, valuesValue;
                if (!payload.TryGetValue("snapshot", out statusValue) ||
                    !payload.TryGetValue("values", out valuesValue))
                    throw new InvalidDataException("Missing verified FC parameter inventory.");
                var status = statusValue as Dictionary<string,object>;
                var rows = valuesValue as Dictionary<string,object>;
                if (status == null || rows == null ||
                    !status.ContainsKey("verified_complete") ||
                    !(status["verified_complete"] is bool) ||
                    !(bool)status["verified_complete"] ||
                    !status.ContainsKey("expected") ||
                    Convert.ToInt32(status["expected"]) != rows.Count)
                    throw new InvalidDataException("Incomplete or inconsistent Pixhawk parameter list.");
                var sb = new StringBuilder();
                sb.AppendLine("PHYSICAL FC PARAMETER INVENTORY (READ-ONLY)");
                sb.AppendLine("Vehicle: " + aircraft + "; count " + rows.Count +
                              "; source: verified Pi PARAM_VALUE frames");
                sb.AppendLine("Readback is a snapshot, not a current write authorization.");
                foreach (var entry in rows.OrderBy(k => k.Key,
                            StringComparer.Ordinal))
                {
                    var data = entry.Value as Dictionary<string,object>;
                    if (data == null) throw new InvalidDataException("Invalid FC parameter row.");
                    object number, encoding;
                    if (!data.TryGetValue("float32", out number) ||
                        !data.TryGetValue("encoding", out encoding))
                        throw new InvalidDataException("Missing FC parameter encoding.");
                    string displayed = number == null ? "UNINTERPRETED" :
                        Convert.ToString(number, CultureInfo.InvariantCulture);
                    sb.Append(entry.Key).Append(" = ").Append(displayed)
                      .Append(" [").Append(Convert.ToString(encoding)).AppendLine("]");
                }
                return sb.ToString();
            }, "Verified onboard parameter inventory");
        }

        private SortedDictionary<string,double> ParseParameterTargets()
        {
            var parsed=new SortedDictionary<string,double>(StringComparer.Ordinal);
            foreach(string line in targets.Lines)
            {
                if(string.IsNullOrWhiteSpace(line))continue;
                var parts=line.Split(new[]{'='},2);
                if(parts.Length!=2)throw new ArgumentException("Use NAME=VALUE per line");
                double value;
                if(!double.TryParse(parts[1].Trim(),NumberStyles.Float,
                    CultureInfo.InvariantCulture,out value) ||
                   double.IsNaN(value)||double.IsInfinity(value))
                    throw new ArgumentException("Invalid parameter value");
                string key=parts[0].Trim();
                if(key.Length<1||key.Length>16||key.Any(x=>!char.IsLetterOrDigit(x)&&x!='_'))
                    throw new ArgumentException("Invalid parameter name");
                if(parsed.ContainsKey(key))
                    throw new ArgumentException("Duplicate parameter: "+key);
                parsed.Add(key,value);
            }
            if(parsed.Count<1||parsed.Count>300)
                throw new ArgumentException("Supply 1–300 parameters");
            return parsed;
        }

        private void SortParameterTargets()
        {
            try
            {
                if(running) return;
                var sorted=ParseParameterTargets();
                targets.Lines=sorted.Select(pair=>pair.Key+"="+
                    pair.Value.ToString("R",CultureInfo.InvariantCulture)).ToArray();
                report.Text=sorted.Count+" parameter targets sorted locally. "+
                    "No parameters were read from or written to the aircraft.";
            }
            catch(Exception ex){report.Text=ex.Message;}
        }

        private async Task CompareParameters()
        {
            SortedDictionary<string,double> parsed;
            try { parsed=ParseParameterTargets(); }
            catch(Exception ex){report.Text=ex.Message;return;}
            if (VihangaLinkSession.UsesDedicatedRelay)
            {
                await Run(() =>
                {
                    var raw = VihangaLinkSession.ReadParameters(Id(), true);
                    var payload = json.Deserialize<Dictionary<string,object>>(raw);
                    object rowsObject;
                    if (!payload.TryGetValue("values", out rowsObject))
                        throw new InvalidDataException("Private parameter snapshot unavailable.");
                    var rows = rowsObject as Dictionary<string,object>;
                    if (rows == null)
                        throw new InvalidDataException("Invalid private parameter snapshot.");
                    int matching = 0, different = 0, unavailable = 0;
                    foreach (var item in parsed)
                    {
                        object rawEntry;
                        var entry = rows.TryGetValue(item.Key, out rawEntry) ?
                            rawEntry as Dictionary<string,object> : null;
                        object reported;
                        if (entry == null || !entry.TryGetValue("float32", out reported) ||
                            reported == null)
                        {
                            unavailable++;
                            continue;
                        }
                        double observed = Convert.ToDouble(reported,
                            CultureInfo.InvariantCulture);
                        double delta = Math.Abs(observed - item.Value);
                        if (delta <= Math.Max(0.00001, Math.Abs(item.Value) * 0.000001))
                            matching++;
                        else different++;
                    }
                    return "PRIVATE READ-ONLY SNAPSHOT" + Environment.NewLine +
                        "Matched: " + matching + Environment.NewLine +
                        "Different: " + different + Environment.NewLine +
                        "Missing/unverified parameter values: " + unavailable +
                        Environment.NewLine +
                        "Integer values are decoded only for verified ArduPilot; no FC write was sent.";
                }, "Private parameter drift");
                return;
            }
            await Run(()=>OperationsApi.Request("POST",
                 "api/v2/vehicles/"+Id()+"/parameters/compare",
                 new{values=parsed}),"Read-only configuration drift");
        }

        private void LoadSurvey()
        {
            using(var dialog=new OpenFileDialog{
                Title="Choose offline survey waypoint draft",
                Filter="Mission Planner waypoint file (*.waypoints)|*.waypoints|Text file (*.txt)|*.txt"})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try
                {
                    var rows=new List<string>();
                    foreach(var line in File.ReadAllLines(dialog.FileName))
                    {
                        var parts=line.Split('\t');
                        if(parts.Length==12 && parts[2]=="3" && parts[3]=="16")
                            rows.Add(parts[8]+","+parts[9]);
                    }
                    if(rows.Count<2||rows.Count>120)
                        throw new InvalidOperationException("File has no supported 2–120 survey waypoints");
                    points.Text=string.Join(Environment.NewLine,rows);
                    lastMissionHash=null;
                    report.Text="Loaded "+rows.Count+" offline points. Revalidate the plan before enabling passive shadow.";
                }
                catch(Exception ex)when(ex is IOException||ex is UnauthorizedAccessException||
                                         ex is InvalidOperationException)
                {
                    report.Text="Unable to load survey draft: "+ex.Message;
                }
            }
        }
    }
}
