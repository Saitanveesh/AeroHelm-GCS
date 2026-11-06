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
        private bool running;
        private string latestCloudReport;

        public AeroHelmIntelWindow(string initialTab = "FLEET")
        {
            Text=VihangaVedikaProduct.Name+" — Fleet Intelligence and Configuration Review";
            StartPosition=FormStartPosition.CenterParent;
            Width=920;Height=740;MinimumSize=new Drawing.Size(750,550);
            var header=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42,Padding=new Padding(8)};
            header.Controls.Add(new Label{Text="Aircraft:",Width=65,
                TextAlign=Drawing.ContentAlignment.MiddleRight,Height=26});
            vehicle.Text="AH-HW-001";vehicle.Width=160;header.Controls.Add(vehicle);
            banner.Text="ANALYSIS ONLY — no mission upload, flight command, calibration or parameter write.";
            banner.AutoSize=true;banner.ForeColor=Drawing.Color.DarkRed;header.Controls.Add(banner);
            var tabs=new TabControl{Dock=DockStyle.Fill};
            var fleet=new TabPage("FLEET");
            var mission=new TabPage("PREFLIGHT");
            var param=new TabPage("PARAMETERS");
            var hist=new TabPage("HISTORY");
            var security=new TabPage("SECURITY");
            var cloudR2=new TabPage("CLOUD R2 / REPORTS");
            tabs.TabPages.AddRange(new[]{fleet,cloudR2,mission,param,hist,security});
            // Touch launcher opens the requested domain directly rather
            // than forcing an additional tab-selection gesture.
            switch ((initialTab ?? "FLEET").ToUpperInvariant())
            {
                case "SECURITY": tabs.SelectedTab = security; break;
                case "CLOUD": tabs.SelectedTab = cloudR2; break;
                case "ANALYTICS": tabs.SelectedTab = hist; break;
                case "PLAN": tabs.SelectedTab = mission; break;
                case "PARAMETERS": tabs.SelectedTab = param; break;
                default: tabs.SelectedTab = fleet; break;
            }

            report.Dock=DockStyle.Bottom;
            report.Height=270;
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
                async()=>await Run(()=>ReadFleetStatus(),"Fleet status")));
            fleetPanel.Controls.Add(ActionButton("Aircraft risk flags",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/vehicles/"+Id()+"/alerts"),"Aircraft alerts")));
            fleetPanel.Controls.Add(new Label{Text="Virtual telemetry is simulated; device reports are unverified. Missing samples remain STALE.",
                AutoSize=true,ForeColor=Drawing.Color.DarkRed});

            var cloudPanel=Panel();cloudR2.Controls.Add(cloudPanel);
            cloudPanel.Controls.Add(new Label {
                Text="Cloud summaries are computed by the server. " +
                     "Physical aircraft control is disabled.",
                Width=700,Height=50
            });
            cloudPanel.Controls.Add(ActionButton("Cloud fleet metrics",
                async()=>await Run(()=>OperationsApi.Request("GET",
                     "api/v1/fleet/metrics"),"Fleet metrics")));
            cloudPanel.Controls.Add(ActionButton("Recent cloud missions",
                async()=>await Run(()=>OperationsApi.Request("GET",
                     "api/v1/missions?limit=50"),"Mission ledger")));
            cloudPanel.Controls.Add(ActionButton("Cloud capabilities",
                async()=>await Run(()=>OperationsApi.Request("GET",
                     "api/v1/capabilities"),"Cloud capabilities")));
            cloudPanel.Controls.Add(ActionButton("Recent audit events",
                async()=>await Run(()=>OperationsApi.Request("GET",
                     "api/v1/events?limit=50"),"Audit events")));
            cloudPanel.Controls.Add(ActionButton("Submit fleet health report",
                async()=>await SubmitCloudReport("fleet_health")));
            cloudPanel.Controls.Add(ActionButton("Submit mission portfolio report",
                async()=>await SubmitCloudReport("mission_portfolio")));
            cloudPanel.Controls.Add(ActionButton("Refresh submitted report",
                async()=>await RefreshCloudReport()));
            cloudPanel.Controls.Add(ActionButton("Ground station settings",()=>{
                using(var window=new VihangaVedikaSettingsWindow())
                    window.ShowDialog(this);
            }));
            cloudPanel.Controls.Add(new Label {
                Text="A pending report means the compute worker has not finished. " +
                     "No local fleet aggregation or remote vehicle commands are performed.",
                Width=700,Height=60
            });

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
            paramPanel.Controls.Add(ActionButton("Compare parameter snapshot",
                async()=>await CompareParameters()));
            paramPanel.Controls.Add(ActionButton("Request read-only parameter refresh from Pi",
                async()=>await Run(()=>OperationsApi.Request("POST",
                    "api/v1/vehicles/"+Id()+"/parameters/refresh"),"Read-only parameter capture")));
            paramPanel.Controls.Add(ActionButton("Inspect parameter snapshot completeness",
                async()=>await ParameterSnapshotStatus()));
            paramPanel.Controls.Add(new Label{Text="Use SETUP/local connection for supported authorized changes. This panel never writes values.",
                AutoSize=true,ForeColor=Drawing.Color.DarkRed});

            var hPanel=Panel();hist.Controls.Add(hPanel);
            hPanel.Controls.Add(ActionButton("Recent live telemetry (up to 100)",
                async()=>await Run(()=>OperationsApi.Request("GET",
                    "api/v2/vehicles/"+Id()+"/history?limit=100"),"Live history")));
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
        }

        private static FlowLayoutPanel Panel()
        {
            return new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,
                FlowDirection=FlowDirection.TopDown,WrapContents=false,
                Padding=new Padding(14)};
        }

        private static Button ActionButton(string label,Action action)
        {
            var button=new Button{Text=label,AutoSize=true,
                MinimumSize=new Drawing.Size(285,44)};
            button.Click+=(s,e)=>action();
            return button;
        }

        private string Id()
        {
            string ident=vehicle.Text.Trim();
            if(ident.Length<3||ident.Length>64||
               ident.Any(ch=>!(char.IsLetterOrDigit(ch)||ch=='-'||ch=='_')))
                throw new InvalidOperationException("Invalid aircraft identifier");
            return Uri.EscapeDataString(ident);
        }

        private async Task Run(Func<string> job,string title)
        {
            if(running)return;
            running=true;
            report.Text=title+": contacting authenticated cloud...";
            try
            {
                // OperationsApi ensures cloud login and token expiry.
                var result=await Task.Run(job);
                report.Text=title+"\r\n"+(result.Length<=32000?
                    result:result.Substring(0,32000)+"\r\n[output truncated]");
            }
            catch(Exception ex)
            {
                report.Text=title+" failed: "+Explain(ex);
            }
            finally {running=false;}
        }

        private string ReadFleetStatus()
        {
            string body;
            try { body=OperationsApi.Request("GET","api/v1/fleet/status"); }
            catch(System.Net.WebException ex)
            {
                var response=ex.Response as System.Net.HttpWebResponse;
                if(response==null || response.StatusCode!=System.Net.HttpStatusCode.NotFound)
                    throw;
                response.Dispose();
                return "Legacy fleet endpoint: "+OperationsApi.Request("GET","api/v2/fleet");
            }
            if(body.Length>131072)throw new InvalidOperationException(
                "Fleet report exceeds the configured response size limit.");
            var source=json.Deserialize<Dictionary<string,object>>(body);
            if(source==null || !source.ContainsKey("cloud_flight_authorized") ||
               !(source["cloud_flight_authorized"] is bool) ||
               (bool)source["cloud_flight_authorized"])
                throw new InvalidOperationException(
                    "Cloud response does not confirm observation-only status.");
            var rows=source.ContainsKey("vehicles") ? source["vehicles"] as object[] : null;
            if(rows==null || rows.Length>128)
                throw new InvalidOperationException("Cloud fleet exceeds the 128-vehicle limit.");
            var text=new StringBuilder();
            text.AppendLine("Fleet observations — not flight-clearance evidence");
            text.AppendLine("Vehicle         Type       Link       Age     Mode");
            foreach(var item in rows)
            {
                var v=item as Dictionary<string,object>;
                if(v==null)continue;
                var telemetry=v.ContainsKey("telemetry") ?
                    v["telemetry"] as Dictionary<string,object> : null;
                var observation=telemetry!=null && telemetry.ContainsKey("observation") ?
                    telemetry["observation"] as Dictionary<string,object> : null;
                double age;
                bool validAge=telemetry!=null && telemetry.ContainsKey("age_s") &&
                    telemetry["age_s"]!=null &&
                    double.TryParse(Convert.ToString(telemetry["age_s"],
                        CultureInfo.InvariantCulture),NumberStyles.Float,
                        CultureInfo.InvariantCulture,out age) &&
                    !double.IsInfinity(age) && !double.IsNaN(age) &&
                    age>=0 && age<=5;
                bool fresh=validAge && telemetry.ContainsKey("fresh") &&
                    telemetry["fresh"] is bool && (bool)telemetry["fresh"] &&
                    telemetry.ContainsKey("session_epoch") &&
                    telemetry["session_epoch"]!=null && observation!=null;
                string kind=v.ContainsKey("kind") ? Convert.ToString(v["kind"]) : "UNKNOWN";
                string id=v.ContainsKey("vehicle_id") ?
                    Convert.ToString(v["vehicle_id"]) : "UNKNOWN";
                if(id.Length>64)id=id.Substring(0,64);
                string type=kind=="virtual"?"SIMULATED":"DEVICE-REPORTED";
                string mode=fresh && observation.ContainsKey("mode") ?
                    Convert.ToString(observation["mode"]) : "UNKNOWN";
                text.AppendLine(id+" | "+type+" | "+(fresh?"FRESH":"STALE")+
                    " | "+(fresh?age.ToString("F1",CultureInfo.InvariantCulture):"—")+
                    " s | "+mode);
            }
            text.AppendLine("Physical flight authority: DISABLED");
            return text.ToString();
        }

        private async Task SubmitCloudReport(string kind)
        {
            if(running)return;
            running=true;
            try
            {
                var response=await Task.Run(()=>OperationsApi.Request(
                    "POST","api/v1/cloud-compute/jobs",
                    new{request_id=Guid.NewGuid().ToString("N"),kind}));
                var item=json.Deserialize<Dictionary<string,object>>(response);
                object id;
                if(item==null || !item.TryGetValue("job_id",out id) ||
                   !Guid.TryParse(Convert.ToString(id),out _))
                    throw new InvalidOperationException("Cloud returned an invalid report ID.");
                latestCloudReport=Convert.ToString(id);
                report.Text="Report queued: "+latestCloudReport+
                    Environment.NewLine+"Refresh to retrieve the cloud-generated result.";
            }
            catch(Exception ex){report.Text="Cloud report unavailable: "+Explain(ex);}
            finally{running=false;}
        }

        private async Task RefreshCloudReport()
        {
            if(string.IsNullOrEmpty(latestCloudReport))
            {
                report.Text="Submit a cloud report first.";return;
            }
            await Run(()=>OperationsApi.Request("GET",
                "api/v1/cloud-compute/jobs/"+Uri.EscapeDataString(latestCloudReport)),
                "Cloud report");
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
            running=true;
            try
            {
                var body=await Task.Run(()=>OperationsApi.Request(
                    "POST","api/v2/missions/validate",plan));
                var response=json.Deserialize<Dictionary<string,object>>(body);
                lastMissionHash=Convert.ToString(response["mission_sha256"]);
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
            // The backend independently validates geometry and hash matching.
            await Run(()=>OperationsApi.Request("POST",
                 "api/v2/missions/shadow/"+Id(),
                 new{plan=plan,expected_sha256=lastMissionHash}),
                 "Mission shadow");
        }

        private async Task ParameterSnapshotStatus()
        {
            if(running)return;
            running=true;
            report.Text="Querying authenticated parameter cache state...";
            try
            {
                var body=await Task.Run(()=>OperationsApi.Request("GET",
                    "api/v1/vehicles/"+Id()+"/parameters"));
                var result=json.Deserialize<Dictionary<string,object>>(body);
                bool complete=result.ContainsKey("verified_complete") &&
                    Convert.ToBoolean(result["verified_complete"]);
                report.Text="Pixhawk parameter snapshot status: "+Convert.ToString(result["status"])+
                    Environment.NewLine+
                    "Received parameters: "+Convert.ToString(result["count"])+
                    " / "+Convert.ToString(result["expected"])+Environment.NewLine+
                    "Verified complete: "+complete+Environment.NewLine+
                    "Captured at (UTC): "+(result.ContainsKey("captured_at")?
                        Convert.ToString(result["captured_at"]):"unknown")+Environment.NewLine+
                    "This is a previously collected Pi snapshot, not a live parameter read. "+
                    "It cannot authorize remote configuration, mission upload or armed operation.";
            }
            catch(Exception ex){report.Text="Parameter inspection failed: "+Explain(ex);}
            finally{running=false;}
        }

        private async Task CompareParameters()
        {
            var parsed=new Dictionary<string,double>();
            try
            {
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
                    parsed[key]=value;
                }
                if(parsed.Count<1||parsed.Count>300)
                    throw new ArgumentException("Supply 1–300 parameters");
            }
            catch(Exception ex){report.Text=ex.Message;return;}
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
