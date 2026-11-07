// AeroHelm supervised maintenance / simulated autonomy UI.
// Authenticated, no local UART, no direct MAVLink command serialization.
// Hardware mission execution is not available in this development stage.
// Copyright AeroHelm contributors, GPL-3.0-or-later.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using MissionPlanner;

namespace AeroHelm.GcsCloud
{
    internal static class OperationsApi
    {
        internal static string Request(string method,string path,object body=null)
        {
            if(string.IsNullOrEmpty(Program.AeroHelmOperatorToken) ||
               DateTime.UtcNow>=Program.AeroHelmTokenExpiresUtc)
                throw new InvalidOperationException(
                    "Cloud login required or expired. Sign in again via CLOUD.");
            Uri origin = AeroHelmWorkspaceSettings.CheckedPrivateUrl(Program.AeroHelmCloudOrigin);
            var url=new Uri(new Uri(origin.GetLeftPart(UriPartial.Authority)+"/"),
                            path.TrimStart('/'));
            var req=(HttpWebRequest)WebRequest.Create(url);
            req.Method=method;
            req.Timeout=12000;req.ReadWriteTimeout=12000;
            req.AllowAutoRedirect=false;
            req.Accept="application/json";
            req.Headers[HttpRequestHeader.Authorization]="Bearer "+Program.AeroHelmOperatorToken;
            if(body!=null)
            {
                byte[] bytes=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                req.ContentType="application/json";
                req.ContentLength=bytes.Length;
                using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);
            }
            using(var response=(HttpWebResponse)req.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }

    internal sealed class AeroHelmOpsWindow : Form
    {
        private readonly TextBox missionText = new TextBox();
        private readonly TextBox missionId = new TextBox();
        private readonly TextBox leaseId = new TextBox();
        private readonly ComboBox kind = new ComboBox();
        private readonly CheckBox onsite = new CheckBox();
        private readonly CheckBox props = new CheckBox();
        private readonly TextBox status = new TextBox();
        private readonly JavaScriptSerializer json=new JavaScriptSerializer();
        private readonly Button start = new Button();
        private readonly Button abort = new Button();
        private readonly Button cal = new Button();
        private readonly System.Windows.Forms.Timer leaseRenewal = new System.Windows.Forms.Timer();
        private string calibrationRequestId = "";
        private bool busy;
        private bool renewing;

        public AeroHelmOpsWindow()
        {
            Text="VIHANGA — Operations | Simulator and Bench Maintenance";
            StartPosition=FormStartPosition.CenterParent;
            Width=860;Height=730;
            MinimumSize=new Drawing.Size(740,620);
            var tabs=new TabControl{Dock=DockStyle.Fill};
            var simulation=new TabPage("AUTONOMY — SIMULATION ONLY");
            var maintenance=new TabPage("MAINTENANCE — PHYSICAL APPROVAL");
            var policy=new TabPage("CAPABILITIES — READ ONLY");
            tabs.TabPages.Add(simulation);tabs.TabPages.Add(maintenance);
            tabs.TabPages.Add(policy);
            Controls.Add(tabs);
            status.Dock=DockStyle.Bottom;status.Height=140;status.Multiline=true;
            status.ScrollBars=ScrollBars.Vertical;status.ReadOnly=true;
            status.Text="No commands sent. Cloud operations require an authenticated login. " +
                "Hardware autonomous flight is blocked; supervised calibration requires " +
                "independent local Pi approval.";
            Controls.Add(status);
            leaseRenewal.Interval=10000; // 30-second lease; renew every 10 seconds
            leaseRenewal.Tick+=async(s,e)=>await RenewSimulationLease();

            var sim=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),
                AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};
            simulation.Controls.Add(sim);
            sim.Controls.Add(new Label{Text="Paste 2-80 waypoint rows: latitude,longitude,relative_altitude_m",
                Width=690,Height=25});
            missionText.Multiline=true;missionText.Width=680;missionText.Height=150;
            missionText.ScrollBars=ScrollBars.Vertical;
            sim.Controls.Add(missionText);
            var load=new Button{Text="Load SURVEY .waypoints draft",Width=255};
            load.Click+=(s,e)=>LoadSurveyFile();
            sim.Controls.Add(load);
            var draft=new Button{Text="Create simulated mission draft",Width=255};
            draft.Click+=async(s,e)=>await CreateDraft();
            sim.Controls.Add(draft);
            sim.Controls.Add(new Label{Text="Mission ID (generated draft)",Width=680});
            missionId.Width=680;missionId.ReadOnly=true;sim.Controls.Add(missionId);
            var lease=new Button{Text="Acquire simulation control lease",Width=255};
            lease.Click+=async(s,e)=>await AcquireLease();
            sim.Controls.Add(lease);
            sim.Controls.Add(new Label{Text="Simulation lease ID (memory only; clear when window closes)",Width=680});
            leaseId.ReadOnly=true;leaseId.Width=680;sim.Controls.Add(leaseId);
            start.Text="START simulation only";start.Width=255;
            abort.Text="ABORT simulation";abort.Width=255;
            start.Click+=async(s,e)=>await MissionAction("start");
            abort.Click+=async(s,e)=>await MissionAction("abort");
            sim.Controls.Add(start);sim.Controls.Add(abort);
            var pause = new Button{Text="PAUSE simulation",Width=255};
            pause.Click+=async(s,e)=>await MissionAction("pause");
            sim.Controls.Add(pause);
            var resume = new Button{Text="RESUME simulation",Width=255};
            resume.Click+=async(s,e)=>await MissionAction("resume");
            sim.Controls.Add(resume);
            var refresh = new Button{Text="Refresh mission progress",Width=255};
            refresh.Click+=async(s,e)=>await RefreshMission();
            sim.Controls.Add(refresh);
            sim.Controls.Add(new Label{Width=680,Height=55,
                Text="AeroHelm production Oracle VM has simulation OFF. Use a dedicated SITL/simulation API instance. " +
                     "Neither these controls nor the waypoint draft can command a real Pixhawk."});

            var maint=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),
                AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};
            maintenance.Controls.Add(maint);
            maint.Controls.Add(new Label{Width=680,Height=65,
                Text="Select one allowed Pixhawk sensor calibration. Hardware must be DISARMED, " +
                     "all propellers removed, and an on-site person must approve the exact request " +
                     "locally at the Pi within 45 seconds. No flight commands are available."});
            kind.DropDownStyle=ComboBoxStyle.DropDownList;
            kind.Items.AddRange(new object[]{"GYRO","BAROMETER","ACCEL_SIMPLE"});
            kind.SelectedIndex=0;kind.Width=250;maint.Controls.Add(kind);
            onsite.Text="A technician is physically present at the aircraft";
            onsite.Width=500;maint.Controls.Add(onsite);
            props.Text="I confirm all propellers are removed and Pixhawk is disarmed";
            props.Width=550;maint.Controls.Add(props);
            cal.Text="Request supervised calibration";cal.Width=260;
            cal.Click+=async(s,e)=>await Calibrate();
            maint.Controls.Add(cal);
            var checkCal = new Button{Text="Refresh calibration result",Width=260};
            checkCal.Click+=async(s,e)=>await RefreshCalibration();
            maint.Controls.Add(checkCal);
            maint.Controls.Add(new Label{Width=650,Height=90,
                Text="After requesting, the local Pi operator must run:\n" +
                    "agent/.venv/bin/python -m agent.supervised_calibration <request-uuid>\n" +
                    "The remote API alone cannot approve the physical calibration."});

            // Read-only policy overview is suitable for checking the cloud
            // before any later Pi/hardware acceptance. Never grants a lease.
            var policyPage=new FlowLayoutPanel {
                Dock=DockStyle.Fill,Padding=new Padding(12),
                AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false
            };
            policy.Controls.Add(policyPage);
            policyPage.Controls.Add(new Label {
                Text="Cloud capabilities are declarations, NOT a physical flight-clearance.",
                Width=680,Height=44
            });
            var capabilityReport=new TextBox {
                Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,
                Width=680,Height=260,
                Text="No authenticated policy response has been requested."
            };
            var refreshPolicy=new Button {Text="Refresh read-only cloud capabilities",Width=310};
            refreshPolicy.Click+=async(s,e)=>{
                refreshPolicy.Enabled=false;
                capabilityReport.Text="Retrieving authenticated operations policy...";
                try
                {
                    capabilityReport.Text=await Api("GET","api/v1/operations/capabilities");
                }
                catch(Exception ex){capabilityReport.Text="Policy unavailable: "+Error(ex);}
                finally {if(!IsDisposed)refreshPolicy.Enabled=true;}
            };
            policyPage.Controls.Add(refreshPolicy);
            policyPage.Controls.Add(capabilityReport);
            // The newer fleet service has a different, virtual-only API.
            // These GET actions do not create leases, missions or device jobs.
            var cloudR2Capabilities=new Button {
                Text="Inspect fleet service capabilities (read-only)",Width=350
            };
            cloudR2Capabilities.Click+=async(s,e)=>{
                cloudR2Capabilities.Enabled=false;
                try
                {
                    capabilityReport.Text="Cloud fleet service capabilities (reported):"+
                        Environment.NewLine+
                        await Api("GET","api/v1/capabilities")+
                        Environment.NewLine+
                        "This is not evidence of supported physical aircraft control.";
                }
                catch(Exception ex)
                {
                    capabilityReport.Text="Fleet service unavailable: "+Error(ex);
                }
                finally { if(!IsDisposed)cloudR2Capabilities.Enabled=true; }
            };
            policyPage.Controls.Add(cloudR2Capabilities);
            var cloudR2Missions=new Button {
                Text="Inspect virtual mission registry (read-only)",Width=350
            };
            cloudR2Missions.Click+=async(s,e)=>{
                cloudR2Missions.Enabled=false;
                try
                {
                    capabilityReport.Text="Virtual mission registry (no commands):"+
                        Environment.NewLine+
                        await Api("GET","api/v1/missions?limit=50");
                }
                catch(Exception ex)
                {
                    capabilityReport.Text="Virtual mission registry unavailable: "+Error(ex);
                }
                finally { if(!IsDisposed)cloudR2Missions.Enabled=true; }
            };
            policyPage.Controls.Add(cloudR2Missions);
            policyPage.Controls.Add(new Label {
                Width=680,Height=80,
                Text="Only a physically present Pi operator may authorize permitted supervised calibration."+
                     Environment.NewLine+
                     "Cloud autonomous missions run in a separate simulator; real aircraft execution remains blocked."
            });

            foreach(var panel in new[]{sim,maint,policyPage})
            {
                var home=new Button {Text="CLOSE OPS",Width=300,Height=42};
                home.Click+=(s,e)=>Close();
                panel.Controls.Add(home);
                panel.Controls.SetChildIndex(home,0);
            }
            BackColor=Drawing.Color.FromArgb(8,8,8);
            ForeColor=Drawing.Color.White;
            AutoScaleMode=AutoScaleMode.Dpi;
            KeyPreview=true;
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){Close();e.Handled=true;}};
            ApplyDarkTheme(this);
        }

        private static void ApplyDarkTheme(Control parent)
        {
            foreach(Control child in parent.Controls)
            {
                if(child is TabPage || child is FlowLayoutPanel || child is TabControl)
                {
                    child.BackColor=Drawing.Color.FromArgb(18,18,18);
                    child.ForeColor=Drawing.Color.White;
                }
                if(child is Button)
                {
                    var button=(Button)child;
                    button.Height=Math.Max(40,button.Height);
                    button.FlatStyle=FlatStyle.Flat;
                    button.FlatAppearance.BorderSize=0;
                    button.BackColor=Drawing.Color.FromArgb(39,39,39);
                    button.ForeColor=Drawing.Color.White;
                    button.UseVisualStyleBackColor=false;
                }
                else if(child is TextBox || child is ComboBox)
                {
                    child.BackColor=Drawing.Color.FromArgb(32,32,32);
                    child.ForeColor=Drawing.Color.White;
                }
                else if(child is Label || child is CheckBox)
                {
                    child.ForeColor=Drawing.Color.FromArgb(222,222,222);
                }
                ApplyDarkTheme(child);
            }
        }

        private void Busy(bool value,string message)
        {
            busy=value;
            start.Enabled=abort.Enabled=cal.Enabled=!value;
            status.Text=message;
        }

        private async Task<string> Api(string method,string path,object payload=null)
        {
            return await Task.Run(()=>OperationsApi.Request(method,path,payload));
        }

        private static string Error(Exception ex)
        {
            var web=ex as WebException;
            if(web!=null)
            {
                var response=web.Response as HttpWebResponse;
                if(response!=null)
                {
                    int code=(int)response.StatusCode;
                    response.Dispose();
                    return "Cloud rejected request (HTTP "+code+").";
                }
            }
            return ex.Message;
        }

        private void LoadSurveyFile()
        {
            using(var dialog=new OpenFileDialog{
                Filter="Mission Planner waypoint draft (*.waypoints)|*.waypoints|Text (*.txt)|*.txt",
                Title="Open OFFLINE SURVEY draft (no upload)"})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try
                {
                    var coordinates=new List<string>();
                    foreach(var line in File.ReadAllLines(dialog.FileName))
                    {
                        var f=line.Trim().Split('\t');
                        if(f.Length!=12 || f[0]=="0" || f[3]!="16" || f[2]!="3")continue;
                        coordinates.Add(f[8]+","+f[9]+","+f[10]);
                    }
                    if(coordinates.Count<2||coordinates.Count>80)
                        throw new ArgumentException("Draft must have 2-80 supported mission waypoints.");
                    missionText.Text=string.Join(Environment.NewLine,coordinates);
                    status.Text="Loaded "+coordinates.Count+" waypoint coordinates for simulation draft only.";
                }
                catch(Exception ex) when(ex is ArgumentException||ex is IOException||
                                           ex is UnauthorizedAccessException)
                {
                    status.Text="Cannot load draft: "+ex.Message;
                }
            }
        }

        private List<object> ParseWaypoints()
        {
            var list=new List<object>();
            foreach(var line in missionText.Lines)
            {
                if(string.IsNullOrWhiteSpace(line))continue;
                var fields=line.Split(',');
                if(fields.Length!=3)throw new ArgumentException("Each row needs latitude,longitude,altitude.");
                double lat,lon,alt;
                if(!double.TryParse(fields[0],NumberStyles.Float,CultureInfo.InvariantCulture,out lat)||
                   !double.TryParse(fields[1],NumberStyles.Float,CultureInfo.InvariantCulture,out lon)||
                   !double.TryParse(fields[2],NumberStyles.Float,CultureInfo.InvariantCulture,out alt))
                    throw new ArgumentException("Invalid numeric waypoint.");
                if(double.IsNaN(lat)||double.IsInfinity(lat)||lat< -90||lat>90||
                   double.IsNaN(lon)||double.IsInfinity(lon)||lon< -180||lon>180||
                   double.IsNaN(alt)||double.IsInfinity(alt)||alt<5||alt>120)
                    throw new ArgumentException("Waypoint outside validated limits.");
                list.Add(new{latitude=lat,longitude=lon,relative_altitude_m=alt});
            }
            if(list.Count<2||list.Count>80)
                throw new ArgumentException("Supply 2-80 waypoints.");
            return list;
        }

        private async Task CreateDraft()
        {
            if(busy)return;
            Busy(true,"Validating and submitting simulation draft...");
            try
            {
                var points=ParseWaypoints();
                var result=json.Deserialize<Dictionary<string,object>>(
                    await Api("POST","api/v1/operations/missions",
                        new{vehicle_id="AH-SIM-001",waypoints=points,speed_mps=3.0}));
                missionId.Text=Convert.ToString(result["mission_id"]);
                status.Text="SIMULATION DRAFT created: "+missionId.Text+
                    ". This is not uploaded to any real aircraft.";
            }catch(Exception ex){status.Text=Error(ex);}
            finally{Busy(false,status.Text);}
        }

        private async Task AcquireLease()
        {
            if(busy)return;
            Busy(true,"Acquiring simulation lease...");
            try
            {
                var reply=await Api("POST","api/v1/leases",new{vehicle_id="AH-SIM-001"});
                var doc=json.Deserialize<Dictionary<string,object>>(reply);
                leaseId.Text=Convert.ToString(doc["lease_id"]);
                leaseRenewal.Start();
                status.Text="Simulation-only lease acquired and scheduled for 10-second renewal. " +
                            "Closing OPS stops renewal; the server expires the lease automatically.";
            }catch(Exception ex){status.Text=Error(ex);}
            finally{Busy(false,status.Text);}
        }

        private async Task MissionAction(string action)
        {
            if(busy)return;
            if(string.IsNullOrWhiteSpace(missionId.Text))
            {
                status.Text="Create a simulation draft first.";return;
            }
            if(action=="start")
            {
                status.Text="Simulation execution requires a fresh operator lease. " +
                    "The current experimental API validates it and denies physical aircraft.";
            }
            Busy(true,"Submitting SIMULATION "+action+"...");
            try
            {
                // OperationsApi applies bearer authentication. A simulation lease
                // must be passed in X-Control-Lease and is never persisted.
                string selectedMission=missionId.Text;
                string selectedLease=leaseId.Text;
                string reply=await Task.Run(()=>RequestWithLease(action,selectedMission,
                    selectedLease));
                if(IsDisposed || Disposing) return;
                var doc=json.Deserialize<Dictionary<string,object>>(reply);
                status.Text="Simulation "+action+": "+Convert.ToString(doc["status"])+
                    ". This does NOT command Pixhawk hardware.";
            }
            catch(Exception ex){status.Text=Error(ex);}
            finally{Busy(false,status.Text);}
        }

        private string RequestWithLease(string action,string id,string lease)
        {
            if(string.IsNullOrWhiteSpace(lease))
                throw new InvalidOperationException("Acquire simulation control lease first.");
            if(string.IsNullOrWhiteSpace(Program.AeroHelmOperatorToken) ||
               DateTime.UtcNow>=Program.AeroHelmTokenExpiresUtc)
                throw new InvalidOperationException("Cloud session expired.");
            var origin=AeroHelmWorkspaceSettings.CheckedPrivateUrl(Program.AeroHelmCloudOrigin);
            string route=action=="renew" ? "api/v1/leases/AH-SIM-001/renew" :
                "api/v1/operations/missions/"+Uri.EscapeDataString(id)+"/"+action;
            var uri=new Uri(new Uri(origin.GetLeftPart(UriPartial.Authority)+"/"),route);
            var req=(HttpWebRequest)WebRequest.Create(uri);
            req.Method="POST";req.AllowAutoRedirect=false;req.Timeout=12000;
            req.Headers[HttpRequestHeader.Authorization]="Bearer "+Program.AeroHelmOperatorToken;
            req.Headers["X-Control-Lease"]=lease;
            byte[] bytes=Encoding.UTF8.GetBytes(action=="start"?
                json.Serialize(new{request_id=Guid.NewGuid().ToString()}):"{}");
            req.ContentType="application/json";req.ContentLength=bytes.Length;
            using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);
            using(var response=(HttpWebResponse)req.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream()))
                return reader.ReadToEnd();
        }

        private async Task RenewSimulationLease()
        {
            if(renewing || string.IsNullOrWhiteSpace(leaseId.Text))return;
            renewing=true;
            try
            {
                string selectedMission=missionId.Text;
                string selectedLease=leaseId.Text;
                await Task.Run(()=>RequestWithLease("renew",selectedMission,selectedLease));
                if(IsDisposed || Disposing) return;
            }
            catch(Exception ex)
            {
                leaseRenewal.Stop();
                if(!IsDisposed && !Disposing)
                    status.Text="Simulation control lease renewal failed: "+Error(ex)+
                    ". The server will abort an active mission after lease expiry.";
            }
            finally { renewing=false; }
        }

        private async Task RefreshMission()
        {
            if(string.IsNullOrWhiteSpace(missionId.Text))return;
            try
            {
                var body=await Api("GET","api/v1/operations/missions/"+
                    Uri.EscapeDataString(missionId.Text));
                var info=json.Deserialize<Dictionary<string,object>>(body);
                status.Text="Mission "+missionId.Text+" | "+
                    Convert.ToString(info["status"])+" | waypoint "+
                    Convert.ToString(info["waypoint_index"])+" | metres "+
                    Convert.ToString(info["completed_distance_m"]);
            }catch(Exception ex){status.Text=Error(ex);}
        }

        private async Task RefreshCalibration()
        {
            if(string.IsNullOrEmpty(calibrationRequestId))
            {
                status.Text="Request a supervised calibration first.";return;
            }
            try
            {
                var body=await Api("GET","api/v1/operations/calibrations/"+
                    Uri.EscapeDataString(calibrationRequestId));
                var info=json.Deserialize<Dictionary<string,object>>(body);
                status.Text="Calibration "+calibrationRequestId+" | "+
                    Convert.ToString(info["status"])+" | "+Convert.ToString(info["detail"]);
            }catch(Exception ex){status.Text=Error(ex);}
        }

        private async Task Calibrate()
        {
            if(busy)return;
            if(!onsite.Checked||!props.Checked)
            {
                status.Text="On-site technician and propeller removal confirmations are mandatory.";
                return;
            }
            Busy(true,"Requesting single-use supervised calibration...");
            try
            {
                string id=Guid.NewGuid().ToString();
                var result=json.Deserialize<Dictionary<string,object>>(
                    await Api("POST","api/v1/operations/calibrations",
                        new{request_id=id,kind=kind.SelectedItem.ToString(),
                            human_at_aircraft=true,props_removed=true}));
                calibrationRequestId=id;
                status.Text="REQUESTED "+kind.SelectedItem+" | ID: "+id+Environment.NewLine+
                    "At the powered-on Pi terminal, operator must run:"+Environment.NewLine+
                    "cd ~/AeroHelm && agent/.venv/bin/python -m agent.supervised_calibration "+id+
                    Environment.NewLine+"Requires AEROHELM_SUPERVISED_CALIBRATION_ENABLED=true " +
                    "on the Pi agent. Expires after 45 seconds; actual result requires COMMAND_ACK.";
            }catch(Exception ex){status.Text=Error(ex);}
            finally{Busy(false,status.Text);}
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                leaseRenewal.Stop();
                leaseRenewal.Dispose();
                leaseId.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
