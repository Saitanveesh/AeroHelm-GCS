// Vihanga Vedika simulator workbench. All jobs are virtual-only.
// Mission Planner and the flight controller retain physical flight authority.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal sealed class VihangaVirtualOpsWindow : Form
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength=131072 };
        private readonly TextBox vehicleId = new TextBox { Text="SIM-01", Width=180 };
        private readonly TextBox vehicleIds = new TextBox { Text="SIM-01", Width=460 };
        private readonly TextBox tasks = new TextBox { Multiline=true, ScrollBars=ScrollBars.Vertical,
            Width=690, Height=144 };
        private readonly TextBox missionId = new TextBox { ReadOnly=true, Width=420 };
        private readonly TextBox planDigest = new TextBox { ReadOnly=true, Width=560 };
        private readonly TextBox status = new TextBox { Multiline=true, ReadOnly=true,
            ScrollBars=ScrollBars.Vertical, Dock=DockStyle.Bottom, Height=175 };
        private readonly NumericUpDown homeLat = Number(-85,85,5,12);
        private readonly NumericUpDown homeLon = Number(-180,180,5,77);
        private readonly NumericUpDown energyWh = Number(1,5000,1,180);
        private readonly NumericUpDown reserve = Number(25,75,1,30);
        private readonly NumericUpDown energyPerKm = Number(1,500,1,25);
        private readonly CheckBox reviewed = new CheckBox {
            Text="I reviewed the SHA-256 plan and confirmed this uses virtual vehicles only.",
            AutoSize=true
        };
        private readonly System.Windows.Forms.Timer renewal = new System.Windows.Forms.Timer();
        private readonly Dictionary<string,int> leaseEpochs = new Dictionary<string,int>();
        private readonly List<string> assignedVehicles = new List<string>();
        private bool busy;
        private bool dispatchAllowed;
        private bool renewing;

        private static NumericUpDown Number(decimal low,decimal high,int places,decimal initial)
        {
            return new NumericUpDown {
                Minimum=low, Maximum=high, DecimalPlaces=places,
                Increment=places==1 ? 1 : 0.0001m,
                Value=initial, Width=115
            };
        }

        public VihangaVirtualOpsWindow()
        {
            Text=VihangaVedikaProduct.Name+" — Virtual Fleet Operations";
            Width=900;Height=775;MinimumSize=new Drawing.Size(790,630);
            StartPosition=FormStartPosition.CenterParent;
            AutoScaleMode=AutoScaleMode.Dpi;
            BackColor=Drawing.Color.FromArgb(23,27,34);
            ForeColor=Drawing.Color.White;
            Font=new Drawing.Font("Segoe UI",10);
            var tabs=new TabControl { Dock=DockStyle.Fill };
            var overview=new TabPage("SERVICE");
            var registry=new TabPage("VIRTUAL VEHICLES");
            var mission=new TabPage("MISSION PLAN");
            var execution=new TabPage("APPROVAL / EXECUTION");
            tabs.TabPages.AddRange(new[]{overview,registry,mission,execution});
            foreach(TabPage tab in tabs.TabPages)
            {
                tab.BackColor=Drawing.Color.FromArgb(27,31,38);
                tab.ForeColor=Drawing.Color.White;
            }
            Controls.Add(tabs);
            Controls.Add(status);
            status.Text="Cloud R2 uses simulated aircraft identities only. " +
                        "A cloud workflow receipt is not an autopilot acknowledgement.";

            var service=Panel(overview);
            Add(service,"Use a trusted private cloud origin and scoped operator identity from CLOUD.");
            Add(service,"Physical aircraft control remains disabled. " +
                        "Local Mission Planner SITL is independent of the cloud job workflow.");
            ActionButton(service,"Check cloud capabilities",async()=>await CheckCapabilities());
            ActionButton(service,"Open native Mission Planner SITL",()=>{
                var main=MissionPlanner.MainV2.instance;
                if(main==null || main.IsDisposed)throw new InvalidOperationException(
                    "Mission Planner is unavailable.");
                Close();
                main.MenuSimulation.Visible=true;
                main.MenuSimulation.PerformClick();
            });
            Add(service,"SITL launch requires an installed simulator and the native " +
                "Mission Planner configuration; this action does not start a flight.");

            var vehicles=Panel(registry);
            Add(vehicles,"Register an isolated SIM-* identity. Repeat for each virtual vehicle.");
            Row(vehicles,"Virtual vehicle ID",vehicleId);
            Row(vehicles,"Home latitude (°)",homeLat);
            Row(vehicles,"Home longitude (°)",homeLon);
            Row(vehicles,"Battery capacity (Wh)",energyWh);
            Row(vehicles,"Reserved energy (%)",reserve);
            Row(vehicles,"Estimated Wh per km",energyPerKm);
            ActionButton(vehicles,"Register virtual vehicle",async()=>await RegisterVirtual());
            ActionButton(vehicles,"List registered aircraft",async()=>await ShowCloud("api/v1/vehicles"));

            var plans=Panel(mission);
            Add(plans,"Virtual IDs, comma-separated (1–8, e.g. SIM-01,SIM-02):");
            plans.Controls.Add(vehicleIds);
            Add(plans,"Tasks, one per line: KEY,LAT,LON,RELATIVE_ALTITUDE_M,ENERGY_WH,PRIORITY");
            Add(plans,"Limits: 1–64 tasks; altitude 10–120 m; energy 0–2000 Wh; priority 0–10.");
            plans.Controls.Add(tasks);
            ActionButton(plans,"Create reviewed virtual plan",async()=>await CreateDraft());
            Row(plans,"Mission ID",missionId);
            Row(plans,"Plan SHA-256",planDigest);
            Add(plans,"Route geometry is not airspace, terrain, weather or geofence clearance.");

            var control=Panel(execution);
            control.Controls.Add(reviewed);
            Add(control,"The mission author cannot approve their own plan. " +
                "Sign in through CLOUD as a separate reviewer, then return here.");
            ActionButton(control,"Approve plan (independent reviewer)",async()=>await Approve());
            Add(control,"Sign in again as the intended virtual mission operator before acquiring leases.");
            ActionButton(control,"Acquire 60-second virtual leases",async()=>await AcquireLeases());
            ActionButton(control,"Start approved virtual workflow",async()=>await StartVirtual());
            ActionButton(control,"Refresh mission and job evidence",async()=>await RefreshMission());
            ActionButton(control,"Abort virtual workflow",async()=>await Abort());
            Add(control,"Virtual device workers must be running independently. " +
                "QUEUED_SIM is not an aircraft command or actual SITL execution.");
            renewal.Interval=20000;
            renewal.Tick+=async(s,e)=>await RenewLeases();
            KeyPreview=true;
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape)Close();};
            Shown+=async(s,e)=>await CheckCapabilities();
        }

        private static FlowLayoutPanel Panel(TabPage page)
        {
            var panel=new FlowLayoutPanel {
                Dock=DockStyle.Fill, Padding=new Padding(18), AutoScroll=true,
                FlowDirection=FlowDirection.TopDown, WrapContents=false
            };
            page.Controls.Add(panel);
            return panel;
        }

        private static void Add(FlowLayoutPanel panel,string text)
        {
            panel.Controls.Add(new Label {
                AutoSize=false,Width=730,Height=text.Length>110?48:30,Text=text,
                ForeColor=Drawing.Color.FromArgb(220,228,235)
            });
        }

        private static void Row(FlowLayoutPanel panel,string title,Control input)
        {
            var line=new FlowLayoutPanel { Width=745,Height=38,WrapContents=false };
            line.Controls.Add(new Label {
                Text=title,Width=230,Height=30,
                TextAlign=Drawing.ContentAlignment.MiddleLeft
            });
            line.Controls.Add(input);
            panel.Controls.Add(line);
        }

        private static void ActionButton(FlowLayoutPanel panel,string text,
                                         Func<Task> action)
        {
            var button=new Button {
                Text=text,Width=360,Height=43,
                FlatStyle=FlatStyle.Flat,Margin=new Padding(0,5,0,6),
                BackColor=Drawing.Color.FromArgb(46,66,83),
                ForeColor=Drawing.Color.White
            };
            button.FlatAppearance.BorderSize=0;
            button.Click+=async(s,e)=>{
                button.Enabled=false;
                try{await action();}
                catch(Exception ex){MessageBox.Show(button.FindForm(),
                    SafeError(ex),VihangaVedikaProduct.Name,
                    MessageBoxButtons.OK,MessageBoxIcon.Warning);}
                finally{if(!button.IsDisposed)button.Enabled=true;}
            };
            panel.Controls.Add(button);
        }

        private static void ActionButton(FlowLayoutPanel panel,string text,Action action)
        {
            ActionButton(panel,text,()=>{action();return Task.CompletedTask;});
        }

        private static string SafeError(Exception ex)
        {
            var web=ex as WebException;
            if(web!=null && web.Response is HttpWebResponse)
            {
                var response=(HttpWebResponse)web.Response;
                int code=(int)response.StatusCode;
                response.Dispose();
                return "Cloud request was rejected (HTTP "+code+"). " +
                       "Confirm private origin, identity, scope and service readiness.";
            }
            if(ex is InvalidOperationException || ex is ArgumentException)
                return ex.Message;
            return "Cloud operation unavailable ("+ex.GetType().Name+").";
        }

        private async Task<T> Run<T>(Func<T> fn)
        {
            if(busy)throw new InvalidOperationException("An operation is already running.");
            busy=true;
            try{return await Task.Run(fn);}
            finally{busy=false;}
        }

        private static Dictionary<string,object> Json(string response)
        {
            if(response.Length>131072)throw new InvalidOperationException(
                "Cloud response exceeds the UI size limit.");
            var result=new JavaScriptSerializer{MaxJsonLength=131072}
                .Deserialize<Dictionary<string,object>>(response);
            if(result==null)throw new InvalidOperationException("Invalid cloud JSON response.");
            return result;
        }

        private static string TextOf(Dictionary<string,object> value,string field)
        {
            object data;
            return value.TryGetValue(field,out data) && data!=null ?
                Convert.ToString(data,CultureInfo.InvariantCulture) : "";
        }

        private static bool Flag(Dictionary<string,object> value,string field)
        {
            object data;
            return value.TryGetValue(field,out data) && data is bool && (bool)data;
        }

        private static string VirtualName(string input)
        {
            string name=(input??"").Trim();
            if(!Regex.IsMatch(name,@"\A(?:SIM-|AH-SIM-)[A-Za-z0-9_-]{1,52}\z",
                              RegexOptions.CultureInvariant))
                throw new ArgumentException("Only SIM-* or AH-SIM-* vehicle IDs are permitted.");
            return name;
        }

        private List<string> Names()
        {
            var names=vehicleIds.Text.Split(',')
                .Select(VirtualName).Distinct(StringComparer.Ordinal).ToList();
            if(names.Count<1 || names.Count>8)
                throw new ArgumentException("Enter 1–8 distinct virtual vehicle IDs.");
            return names;
        }

        private List<object> Tasks()
        {
            var parsed=new List<object>();
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(string line in tasks.Lines)
            {
                if(string.IsNullOrWhiteSpace(line))continue;
                var p=line.Split(',').Select(x=>x.Trim()).ToArray();
                if(p.Length!=6 || !Regex.IsMatch(p[0],@"\A[A-Za-z0-9_-]{3,64}\z"))
                    throw new ArgumentException("Each task needs KEY,LAT,LON,ALT,WH,PRIORITY.");
                double lat,lon,alt,wh;
                int priority;
                if(!keys.Add(p[0]) ||
                   !double.TryParse(p[1],NumberStyles.Float,CultureInfo.InvariantCulture,out lat) ||
                   !double.TryParse(p[2],NumberStyles.Float,CultureInfo.InvariantCulture,out lon) ||
                   !double.TryParse(p[3],NumberStyles.Float,CultureInfo.InvariantCulture,out alt) ||
                   !double.TryParse(p[4],NumberStyles.Float,CultureInfo.InvariantCulture,out wh) ||
                   !int.TryParse(p[5],NumberStyles.Integer,CultureInfo.InvariantCulture,out priority) ||
                   double.IsNaN(lat) || double.IsInfinity(lat) || lat < -85 || lat > 85 ||
                   double.IsNaN(lon) || double.IsInfinity(lon) || lon < -180 || lon > 180 ||
                   double.IsNaN(alt) || double.IsInfinity(alt) || alt < 10 || alt > 120 ||
                   double.IsNaN(wh) || double.IsInfinity(wh) || wh < 0 || wh > 2000 ||
                   priority < 0 || priority > 10)
                    throw new ArgumentException("Task contains duplicate ID or an out-of-range value.");
                parsed.Add(new {
                    key=p[0], latitude=lat, longitude=lon,
                    relative_altitude_m=alt,required_energy_wh=wh,priority
                });
            }
            if(parsed.Count<1 || parsed.Count>64)
                throw new ArgumentException("Enter 1–64 valid virtual tasks.");
            return parsed;
        }

        private async Task CheckCapabilities()
        {
            dispatchAllowed=false;
            leaseEpochs.Clear();renewal.Stop();
            try
            {
                var cap=await Run(()=>Json(OperationsApi.Request(
                    "GET","api/v1/capabilities")));
                if(!Flag(cap,"cloud_fleet_control_plane") ||
                   Flag(cap,"physical_aircraft_dispatch") ||
                   !Flag(cap,"durable_state"))
                    throw new InvalidOperationException(
                        "Cloud R2 capability boundary is missing or unsafe.");
                dispatchAllowed=Flag(cap,"virtual_dispatch_enabled");
                status.Text=dispatchAllowed ?
                    "Cloud virtual dispatch is enabled. This is a simulated workflow only." :
                    "Cloud is connected; virtual dispatch is disabled. " +
                    "Review the dedicated simulator service before starting work.";
            }
            catch(Exception ex)
            {
                status.Text="Cloud R2 not ready: "+SafeError(ex)+Environment.NewLine+
                    "An M1 operator token may not have the required R2 OIDC scopes.";
            }
        }

        private async Task RegisterVirtual()
        {
            var id=VirtualName(vehicleId.Text);
            var profile=new {
                home_lat=(double)homeLat.Value,home_lon=(double)homeLon.Value,
                energy_wh=(double)energyWh.Value,
                reserve_fraction=(double)reserve.Value/100.0,
                energy_wh_per_km=(double)energyPerKm.Value
            };
            var response=await Run(()=>Json(OperationsApi.Request(
                "POST","api/v1/vehicles",
                new{vehicle_id=id,kind="virtual",profile})));
            status.Text="Registered virtual vehicle "+id+". "+TextOf(response,"vehicle_id")+
                Environment.NewLine+"No physical flight controller was connected.";
        }

        private async Task ShowCloud(string route)
        {
            var result=await Run(()=>OperationsApi.Request("GET",route));
            status.Text=result.Length<=32000?result:result.Substring(0,32000)+" [truncated]";
        }

        private async Task CreateDraft()
        {
            var names=Names();
            var specifications=Tasks();
            var doc=await Run(()=>Json(OperationsApi.Request(
                "POST","api/v1/missions",new {
                    request_id=Guid.NewGuid().ToString("N"),
                    vehicle_ids=names,tasks=specifications
                })));
            if(Flag(doc,"physical_flight_authorized") ||
               TextOf(doc,"execution_target")!="VIRTUAL_SIMULATOR_ONLY")
                throw new InvalidOperationException(
                    "Server did not confirm simulator-only plan execution.");
            string hash=TextOf(doc,"plan_sha256"),id=TextOf(doc,"id");
            if(!Regex.IsMatch(hash,@"\A[a-f0-9]{64}\z") ||
               !Guid.TryParse(id,out _))
                throw new InvalidOperationException("Cloud did not issue valid plan evidence.");
            missionId.Text=id;
            planDigest.Text=hash;
            reviewed.Checked=false;
            leaseEpochs.Clear();renewal.Stop();
            assignedVehicles.Clear();
            var specification=doc["specification"] as Dictionary<string,object>;
            var assignments=specification!=null && specification.ContainsKey("assignments") ?
                specification["assignments"] as object[] : null;
            if(assignments==null)throw new InvalidOperationException(
                "Cloud response did not include assigned virtual vehicles.");
            foreach(var entry in assignments)
            {
                var item=entry as Dictionary<string,object>;
                if(item!=null)
                {
                    var assigned=VirtualName(TextOf(item,"vehicle_id"));
                    if(!assignedVehicles.Contains(assigned))assignedVehicles.Add(assigned);
                }
            }
            if(assignedVehicles.Count==0)throw new InvalidOperationException(
                "Cloud did not assign any virtual work.");
            status.Text="DRAFT "+id+Environment.NewLine+"SHA-256: "+hash+
                Environment.NewLine+"Assigned virtual vehicles: "+
                string.Join(", ",assignedVehicles)+
                Environment.NewLine+"A separate reviewer must approve the immutable plan.";
        }

        private void NeedPlan()
        {
            if(!Guid.TryParse(missionId.Text,out _) ||
               !Regex.IsMatch(planDigest.Text,@"\A[a-f0-9]{64}\z"))
                throw new InvalidOperationException("Create a virtual plan with valid digest first.");
        }

        private async Task Approve()
        {
            NeedPlan();
            if(!reviewed.Checked)throw new InvalidOperationException(
                "Review and confirm the SHA-256 plan first.");
            var id=missionId.Text;
            var hash=planDigest.Text;
            var doc=await Run(()=>Json(OperationsApi.Request(
                "POST","api/v1/missions/"+Uri.EscapeDataString(id)+"/approve",
                new{request_id=Guid.NewGuid().ToString("N"),plan_sha256=hash})));
            if(Flag(doc,"physical_flight_authorized") ||
               TextOf(doc,"status")!="APPROVED_SIM")
                throw new InvalidOperationException("Cloud did not record a virtual plan approval.");
            status.Text="APPROVED_SIM by "+TextOf(doc,"approved_by")+
                Environment.NewLine+"Acquire leases using the intended mission operator.";
        }

        private async Task AcquireLeases()
        {
            NeedPlan();
            if(assignedVehicles.Count==0)throw new InvalidOperationException(
                "No assigned virtual vehicles; refresh/create plan.");
            var epochs=new Dictionary<string,int>();
            foreach(var id in assignedVehicles)
            {
                var ident=id;
                var response=await Run(()=>Json(OperationsApi.Request(
                    "POST","api/v1/vehicles/"+Uri.EscapeDataString(ident)+"/lease",
                    new{duration_seconds=60})));
                object epoch;
                if(!response.TryGetValue("epoch",out epoch) ||
                   !(epoch is int) || (int)epoch<1)
                    throw new InvalidOperationException("Cloud did not confirm a lease epoch.");
                epochs[id]=(int)epoch;
            }
            leaseEpochs.Clear();
            foreach(var pair in epochs)leaseEpochs[pair.Key]=pair.Value;
            renewal.Start();
            status.Text="Exclusive virtual leases acquired for "+string.Join(", ",assignedVehicles)+
                ". Lease epochs are not hardware control authority.";
        }

        private async Task StartVirtual()
        {
            NeedPlan();
            if(!dispatchAllowed)throw new InvalidOperationException(
                "Virtual execution is disabled; check cloud capabilities.");
            if(assignedVehicles.Any(id=>!leaseEpochs.ContainsKey(id)))
                throw new InvalidOperationException("Acquire all assigned virtual leases.");
            if(!reviewed.Checked)throw new InvalidOperationException(
                "Review the mission digest before starting virtual work.");
            var result=await Run(()=>Json(OperationsApi.Request(
                "POST","api/v1/missions/"+Uri.EscapeDataString(missionId.Text)+"/start",
                new{request_id=Guid.NewGuid().ToString("N")})));
            if(Flag(result,"physical_flight_authorized") ||
               TextOf(result,"execution_target")!="VIRTUAL_SIMULATOR_ONLY")
                throw new InvalidOperationException("Cloud did not confirm virtual-only execution.");
            status.Text="Virtual work: "+TextOf(result,"status")+
                Environment.NewLine+"The independent simulator must claim the jobs. " +
                "This UI does not send flight-controller commands.";
        }

        private async Task RefreshMission()
        {
            NeedPlan();
            var result=await Run(()=>Json(OperationsApi.Request(
                "GET","api/v1/missions/"+Uri.EscapeDataString(missionId.Text))));
            if(Flag(result,"physical_flight_authorized"))
                throw new InvalidOperationException("Unexpected physical flight authority.");
            string state=TextOf(result,"status");
            if(state!="RUNNING_SIM" && state!="DRAFT" && state!="APPROVED_SIM")
            {renewal.Stop();leaseEpochs.Clear();}
            status.Text="Mission "+missionId.Text+Environment.NewLine+
                "Server state: "+state+Environment.NewLine+
                "Real aircraft execution: DISABLED"+
                Environment.NewLine+"Job results must be reviewed against simulator logs.";
        }

        private async Task Abort()
        {
            NeedPlan();
            var decision=MessageBox.Show(this,
                "Abort the cloud virtual workflow? This does not command an aircraft.",
                "Confirm virtual abort",MessageBoxButtons.YesNo,MessageBoxIcon.Question);
            if(decision!=DialogResult.Yes)return;
            var result=await Run(()=>Json(OperationsApi.Request(
                "POST","api/v1/missions/"+Uri.EscapeDataString(missionId.Text)+"/abort")));
            renewal.Stop();leaseEpochs.Clear();
            status.Text="Virtual mission: "+TextOf(result,"status")+
                ". An HTTP receipt is not a flight-controller acknowledgement.";
        }

        private async Task RenewLeases()
        {
            if(renewing || leaseEpochs.Count==0 || busy)return;
            renewing=true;
            try
            {
                var items=leaseEpochs.ToArray();
                foreach(var item in items)
                {
                    var pair=item;
                    await Task.Run(()=>OperationsApi.Request("POST",
                        "api/v1/vehicles/"+Uri.EscapeDataString(pair.Key)+"/lease/renew",
                        new{duration_seconds=60,expected_epoch=pair.Value}));
                }
            }
            catch(Exception ex)
            {
                renewal.Stop();leaseEpochs.Clear();
                if(!IsDisposed)
                    status.Text="Lease renewal unavailable: "+SafeError(ex)+
                        Environment.NewLine+
                        "Execution status is uncertain. Reconcile with the cloud; do not retry commands.";
            }
            finally{renewing=false;}
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing){renewal.Stop();renewal.Dispose();leaseEpochs.Clear();}
            base.Dispose(disposing);
        }
    }
}
