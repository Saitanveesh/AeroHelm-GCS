// AeroHelm PATHS — explicit cloud-observation versus direct-pilot link choice.
// The controller's normal RC/failsafe policy is external to this UI.
// This form NEVER opens a serial port, sends MAVLink, changes FC parameters,
// or transfers flight authority automatically.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Windows.Forms;
using MissionPlanner;

namespace AeroHelm.GcsCloud
{
    internal enum AeroHelmLinkPath { Cancel, CloudReadOnly, PilotDirect, CloudAttitudePreview }

    internal sealed class AeroHelmLinkSelector : Form
    {
        internal AeroHelmLinkPath Selection { get; private set; }
        private readonly Label cloudEvidence = new Label();

        internal AeroHelmLinkSelector()
        {
            Selection = AeroHelmLinkPath.Cancel;
            Text = VihangaVedikaProduct.Name + " — Select Telemetry Link";
            Width = 850; Height = 660;
            MinimumSize = new Drawing.Size(680, 570);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Drawing.Color.FromArgb(9,9,9);
            ForeColor = Drawing.Color.White;
            Font = new Drawing.Font("Segoe UI", 11f);
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
                Padding = new Padding(22), BackColor = BackColor
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 85));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            Controls.Add(layout);
            layout.Controls.Add(new Label {
                Dock = DockStyle.Fill,
                Font = new Drawing.Font("Segoe UI Semibold", 15f),
                ForeColor = Drawing.Color.FromArgb(236,236,236),
                Text = "FLIGHT DATA / SELECT CONNECTION PATH" + Environment.NewLine +
                       "A cloud viewer is NOT a flight-control link."
            },0,0);

            var cloud = Action(
                "CLOUD VIEW — receive-only / Tailscale HTTPS + localhost",
                "Requires a real Pixhawk HEARTBEAT/ATTITUDE over the Pi→Oracle path. " +
                "No parameter writes, missions, calibration or commands can pass.");
            cloud.BackColor = Drawing.Color.FromArgb(29,48,62);
            cloud.Click += (sender,args)=>Select(AeroHelmLinkPath.CloudReadOnly);
            layout.Controls.Add(cloud,0,1);

            var direct = Action(
                "PILOT BACKUP — native USB / telemetry-radio COM",
                "Use Mission Planner's normal COM port and baud controls; " +
                "hardware radio must be independently wired and bench-tested. " +
                "This selection does NOT connect or arm automatically.");
            // Amber distinguishes deliberate local pilot authority from the
            // blue receive-only cloud viewer; neither color asserts readiness.
            direct.BackColor = Drawing.Color.FromArgb(70,55,34);
            direct.Click += (sender,args)=>Select(AeroHelmLinkPath.PilotDirect);
            layout.Controls.Add(direct,0,2);
            var preview=Action(
                "CLOUD ATTITUDE — real-only visual monitor",
                "Independent observation panel for actual cloud ATTITUDE data. "+
                "It does NOT connect Mission Planner, generate fake readings or control the aircraft.");
            preview.Click+=(sender,args)=>Select(AeroHelmLinkPath.CloudAttitudePreview);
            layout.Controls.Add(preview,0,3);
            layout.Controls.Add(new Label {
                Dock=DockStyle.Fill,
                ForeColor=Drawing.Color.FromArgb(205,187,153),
                Text="NO AUTO-FAILOVER: cloud loss only raises an alert. " +
                     "The pilot deliberately selects the independent local radio/USB path. " +
                     "Never treat network reconnection as permission to transmit commands."
            },0,4);
            cloudEvidence.Dock=DockStyle.Fill;
            cloudEvidence.Text="Cloud link assessment: checking authenticated path...";
            cloudEvidence.ForeColor=Drawing.Color.FromArgb(205,205,205);
            cloudEvidence.AutoEllipsis=true;
            cloudEvidence.AccessibleName="Cloud diagnostic evidence; not flight control authorization";
            layout.Controls.Add(cloudEvidence,0,5);
            Shown+=async(sender,args)=>await RefreshEvidenceAsync();
            var cancel=new Button {
                Text="CANCEL",Dock=DockStyle.Right,Width=160,Height=44,
                DialogResult=DialogResult.Cancel,
                FlatStyle=FlatStyle.Flat,
                ForeColor=ForeColor,BackColor=Drawing.Color.FromArgb(43,43,43)
            };
            layout.Controls.Add(cancel,0,6);
            CancelButton=cancel;
            KeyPreview=true;
            KeyDown+=(s,e)=>{
                if(e.KeyCode==Keys.Escape)
                {
                    DialogResult=DialogResult.Cancel;
                    Close();e.Handled=true;e.SuppressKeyPress=true;
                }
            };
        }

        private async Task RefreshEvidenceAsync()
        {
            try
            {
                string data=await Task.Run(()=>
                    OperationsApi.Request("GET","api/v1/mavlink/paths"));
                if(IsDisposed||Disposing)return;
                var json=new JavaScriptSerializer();
                var record=json.Deserialize<Dictionary<string,object>>(data);
                var paths=record["paths"] as object[];
                if(paths==null || paths.Length<2)
                    throw new InvalidOperationException("Incomplete cloud link assessment");
                var cloud=paths[0] as Dictionary<string,object>;
                if(cloud==null)
                    throw new InvalidOperationException("Invalid cloud link status");
                cloudEvidence.Text="CLOUD / "+Convert.ToString(cloud["status"])+
                    " | GCS viewers "+Convert.ToString(cloud["viewer_count"])+
                    "     •     DIRECT / independent; not observable by Oracle";
            }
            catch(WebException ex)
            {
                if(IsDisposed||Disposing)return;
                var response=ex.Response as HttpWebResponse;
                cloudEvidence.Text=response!=null &&
                    response.StatusCode==HttpStatusCode.NotFound ?
                    "CLOUD / legacy Oracle API — upgrade path monitoring separately" :
                    "CLOUD / path check unavailable — use LINK diagnostics";
                if(response!=null)response.Dispose();
            }
            catch(Exception)
            {
                if(!IsDisposed&&!Disposing)
                    cloudEvidence.Text="CLOUD / path state unknown — direct backup is independent";
            }
        }

        private static Button Action(string title, string details)
        {
            var button=new Button {
                Text=title+Environment.NewLine+details,
                TextAlign=Drawing.ContentAlignment.MiddleLeft,
                Dock=DockStyle.Fill,FlatStyle=FlatStyle.Flat,
                ForeColor=Drawing.Color.White,
                BackColor=Drawing.Color.FromArgb(31,31,31),
                Font=new Drawing.Font("Segoe UI",11f),
                Padding=new Padding(18,8,12,8),
                UseVisualStyleBackColor=false,
                AccessibleName=title,
                AccessibleDescription=details,
                TabStop=true
            };
            button.FlatAppearance.BorderColor=Drawing.Color.FromArgb(82,82,82);
            button.FlatAppearance.BorderSize=1;
            return button;
        }

        private void Select(AeroHelmLinkPath path)
        {
            Selection=path;
            DialogResult=DialogResult.OK;
            Close();
        }
    }

    // Central native connection point used by both the PATHS toolbar and
    // TOUCH/FLIGHT. This does NOT take ownership of the Pixhawk serial link.
    internal static class AeroHelmLinkNavigation
    {
        internal static bool Open(MainV2 main, AeroHelmLinkPath selected, bool? cloudHeartbeatLive)
        {
            if (main == null || main.IsDisposed || selected == AeroHelmLinkPath.Cancel)
                return false;
            if (selected == AeroHelmLinkPath.CloudAttitudePreview)
            {
                using(var window=new AeroHelmCloudAttitudeWindow())
                    window.ShowDialog(main);
                return true;
            }
            if (selected == AeroHelmLinkPath.PilotDirect)
            {
                // A completely independent native COM/telemetry-radio
                // connection module. No cloud credential, TCP bridge, UART
                // handover or automatic switch is required. An on-site pilot
                // must choose an observed port and explicitly authorize it.
                using(var window=new AeroHelmPilotDirectWindow(main))
                    window.ShowDialog(main);
                return true;
            }

            if (!string.Equals(Environment.GetEnvironmentVariable(
                "AEROHELM_CLOUD_READONLY"),"1",StringComparison.Ordinal))
            {
                MessageBox.Show(main,"Cloud flight-data viewing requires the "+
                    "receive-only session profile; restart via the verified "+
                    "AeroHelm launcher.", "AeroHelm cloud blocked",
                    MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return false;
            }
            var bridge=AeroHelmCloudPlugin.ActiveNativeBridge;
            if (bridge==null)
            {
                MessageBox.Show(main,"The AeroHelm native receiver plugin is "+
                    "not loaded. Cloud HUD cannot be started. Direct pilot "+
                    "telemetry remains available through Mission Planner.",
                    "AeroHelm cloud plugin missing",
                    MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return false;
            }
            if (!bridge.IsListening && !bridge.Start())
            {
                MessageBox.Show(main,"Cannot start the loopback receiver."+
                    Environment.NewLine+bridge.Summary+
                    Environment.NewLine+"Inspect TCP 5760 and sign-in state. "+
                    "No unknown process will be terminated.",
                    "AeroHelm cloud path unavailable",
                    MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return false;
            }
            if(cloudHeartbeatLive==false)
            {
                MessageBox.Show(main,
                    "Cloud reports no fresh real Pixhawk HEARTBEAT. "+
                    "Generic frame counts cannot initialize Mission Planner. "+
                    "Use LINK diagnostics or direct physical telemetry radio. "+
                    "No fake HEARTBEAT will be emitted.",
                    "Vihanga Vedika — Heartbeat Required",
                    MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return false;
            }
            // Never switch an already active native link without the pilot's
            // explicit disconnect; a direct radio may carry command authority.
            if(MainV2.comPort.BaseStream.IsOpen)
            {
                main.MenuFlightData.PerformClick();
                MessageBox.Show(main,
                    "A native connection is already open. Disconnect it "+
                    "deliberately before selecting a different path. "+
                    "AeroHelm will not change control authority automatically.",
                    "Vihanga Vedika — Active Native Connection",
                    MessageBoxButtons.OK,MessageBoxIcon.Information);
                return false;
            }

            main.MenuFlightData.PerformClick();
            try
            {
                main.doConnect(MainV2.comPort,"TCP","57600",false,false);
                return true;
            }
            catch(Exception ex)
            {
                MessageBox.Show(main,
                    "Cloud viewer could not establish a real HEARTBEAT. "+
                    "Inspect LINK and upstream ATTITUDE counters; no "+
                    "autopilot control or stream-rate request was made."+
                    Environment.NewLine+ex.Message,
                    "Vihanga Vedika — Telemetry Not Verified",
                    MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return false;
            }
        }
    }

}
