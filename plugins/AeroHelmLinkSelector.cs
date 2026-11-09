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

    // Drawn cards are insulated from Mission Planner's global button theme.
    // Focus and Enter/Space activate the same explicit path selection.
    internal sealed class VihangaLinkChoice : Control
    {
        private readonly string heading;
        private readonly string description;
        private readonly Drawing.Color accent;
        private readonly Drawing.Font headingFont =
            new Drawing.Font("Segoe UI Semibold",13f,Drawing.FontStyle.Bold);
        private readonly Drawing.Font descriptionFont =
            new Drawing.Font("Segoe UI",10f);
        private bool hovered;

        internal VihangaLinkChoice(string title,string summary,Drawing.Color stripe)
        {
            heading=title;
            description=summary;
            accent=stripe;
            Dock=DockStyle.Fill;
            Margin=new Padding(0,4,0,4);
            Cursor=Cursors.Hand;
            TabStop=true;
            AccessibleRole=AccessibleRole.PushButton;
            AccessibleName=title;
            AccessibleDescription=summary;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var rect=ClientRectangle;
            float dpi=Math.Max(1f,e.Graphics.DpiX/96f);
            e.Graphics.Clear(hovered ? Drawing.Color.FromArgb(40,47,55) :
                Drawing.Color.FromArgb(27,32,39));
            using(var brush=new Drawing.SolidBrush(accent))
                e.Graphics.FillRectangle(brush,0,0,Math.Max(3,(int)(4*dpi)),rect.Height);
            using(var pen=new Drawing.Pen(Focused ? accent :
                Drawing.Color.FromArgb(64,70,77),Focused?2f:1f))
                e.Graphics.DrawRectangle(pen,1,1,Math.Max(1,rect.Width-3),
                    Math.Max(1,rect.Height-3));

            int x=Math.Max(18,(int)(22*dpi));
            int top=Math.Max(7,(int)(9*dpi));
            int width=Math.Max(1,rect.Width-x-Math.Max(10,(int)(14*dpi)));
            int titleHeight=TextRenderer.MeasureText(heading,headingFont,
                new Drawing.Size(width,120),TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix).Height;
            int titleBottom=top+titleHeight+2;
            TextRenderer.DrawText(e.Graphics,heading,headingFont,
                new Drawing.Rectangle(x,top,width,titleHeight+2),
                Drawing.Color.FromArgb(245,247,249),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            int subtitleTop=titleBottom+Math.Max(2,(int)(3*dpi));
            TextRenderer.DrawText(e.Graphics,description,descriptionFont,
                new Drawing.Rectangle(x,subtitleTop,width,
                    Math.Max(1,rect.Height-subtitleTop-Math.Max(5,(int)(7*dpi)))),
                Drawing.Color.FromArgb(184,193,202),
                TextFormatFlags.Left | TextFormatFlags.WordBreak |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered=true; Invalidate(); base.OnMouseEnter(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            hovered=false; Invalidate(); base.OnMouseLeave(e);
        }
        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate(); base.OnGotFocus(e);
        }
        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate(); base.OnLostFocus(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if(e.Button==MouseButtons.Left) Focus();
            base.OnMouseDown(e);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if(e.KeyCode==Keys.Enter || e.KeyCode==Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled=true;
                e.SuppressKeyPress=true;
                return;
            }
            base.OnKeyDown(e);
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing){headingFont.Dispose();descriptionFont.Dispose();}
            base.Dispose(disposing);
        }
    }

    internal sealed class AeroHelmLinkSelector : Form
    {
        internal AeroHelmLinkPath Selection { get; private set; }
        private readonly Label cloudEvidence = new Label();

        internal AeroHelmLinkSelector()
        {
            Selection=AeroHelmLinkPath.Cancel;
            Text="Vihanga GCS | Connections";
            Width=760;Height=440;
            MinimumSize=new Drawing.Size(580,395);
            StartPosition=FormStartPosition.CenterParent;
            AutoScaleMode=AutoScaleMode.Dpi;
            BackColor=Drawing.Color.FromArgb(17,21,27);
            ForeColor=Drawing.Color.FromArgb(239,243,247);
            Font=new Drawing.Font("Segoe UI",10f);

            var layout=new TableLayoutPanel {
                Dock=DockStyle.Fill, ColumnCount=1,RowCount=6,
                Padding=new Padding(20,16,20,14),BackColor=BackColor
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int height in new[]{66,94,94,40})
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            Controls.Add(layout);

            var header=new TableLayoutPanel {
                Dock=DockStyle.Fill,ColumnCount=1,RowCount=2
            };
            header.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            header.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            header.Controls.Add(new Label {
                Text="Connections",Dock=DockStyle.Fill,
                Font=new Drawing.Font("Segoe UI Semibold",20f,Drawing.FontStyle.Bold),
                ForeColor=ForeColor,
                TextAlign=Drawing.ContentAlignment.MiddleLeft
            },0,0);
            header.Controls.Add(new Label {
                Text="Select a telemetry source",Dock=DockStyle.Fill,
                ForeColor=Drawing.Color.FromArgb(166,177,190),
                TextAlign=Drawing.ContentAlignment.MiddleLeft
            },0,1);
            layout.Controls.Add(header,0,0);

            var cloud=new VihangaLinkChoice("Cloud telemetry",
                "Read-only   |   FC -> Pi -> Oracle -> native DATA HUD and GPS map",
                Drawing.Color.FromArgb(66,155,210));
            cloud.Click+=(s,e)=>Select(AeroHelmLinkPath.CloudReadOnly);
            layout.Controls.Add(cloud,0,1);

            var direct=new VihangaLinkChoice("Direct telemetry",
                "Pilot connection   |   USB or telemetry-radio COM port",
                Drawing.Color.FromArgb(211,163,83));
            direct.Click+=(s,e)=>Select(AeroHelmLinkPath.PilotDirect);
            layout.Controls.Add(direct,0,2);

            layout.Controls.Add(new Label {
                Dock=DockStyle.Fill,
                Text="Connection changes are manual. No automatic radio switchover.",
                ForeColor=Drawing.Color.FromArgb(175,186,198),
                TextAlign=Drawing.ContentAlignment.MiddleLeft
            },0,3);

            cloudEvidence.Dock=DockStyle.Fill;
            cloudEvidence.Text="Cloud status: checking...";
            cloudEvidence.ForeColor=Drawing.Color.FromArgb(192,203,215);
            cloudEvidence.TextAlign=Drawing.ContentAlignment.MiddleLeft;
            cloudEvidence.AccessibleName="Cloud status; read-only observation";
            layout.Controls.Add(cloudEvidence,0,4);

            var cancel=new Button {
                Text="Close",Dock=DockStyle.Right,Width=112,Height=38,
                DialogResult=DialogResult.Cancel,FlatStyle=FlatStyle.Flat,
                ForeColor=ForeColor,BackColor=Drawing.Color.FromArgb(42,49,57),
                UseVisualStyleBackColor=false
            };
            cancel.FlatAppearance.BorderColor=Drawing.Color.FromArgb(85,94,104);
            layout.Controls.Add(cancel,0,5);
            CancelButton=cancel;
            KeyPreview=true;
            KeyDown+=(s,e)=>{
                if(e.KeyCode==Keys.Escape)
                {
                    DialogResult=DialogResult.Cancel;
                    Close();
                    e.Handled=true;
                }
            };
            Shown+=async(s,e)=>await RefreshEvidenceAsync();
        }

        private async Task RefreshEvidenceAsync()
        {
            try
            {
                if (VihangaLinkSession.UsesDedicatedRelay)
                {
                    if (!VihangaLinkSession.Authenticated)
                    {
                        cloudEvidence.Text = "Private relay: sign in to view telemetry.";
                        return;
                    }
                    string verified = await Task.Run(() =>
                    {
                        Uri origin = new Uri(VihangaLinkSession.TargetOrigin + "/");
                        var request = (HttpWebRequest)WebRequest.Create(
                            new Uri(origin, "api/v1/mavlink/health"));
                        request.Method = "GET";
                        request.AllowAutoRedirect = false;
                        request.Timeout = 8000;
                        using (var response = (HttpWebResponse)request.GetResponse())
                        using (var reader = new System.IO.StreamReader(response.GetResponseStream()))
                        {
                            if (response.StatusCode != HttpStatusCode.OK)
                                throw new WebException("Private relay health unavailable.");
                            var data = new JavaScriptSerializer()
                                .Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
                            object online, hb, att;
                            bool connected = data.TryGetValue("device_online", out online) &&
                                             online is bool && (bool)online;
                            bool heartbeat = data.TryGetValue(
                                "native_hud_heartbeat_ready", out hb) && hb is bool && (bool)hb;
                            bool attitude = data.TryGetValue(
                                "attitude_stream_active", out att) && att is bool && (bool)att;
                            return !connected ? "Private relay: Pi disconnected" :
                                heartbeat && attitude ? "Private relay: heartbeat + attitude live" :
                                heartbeat ? "Private relay: ATTITUDE missing" :
                                "Private relay: waiting for MAVLink heartbeat";
                        }
                    });
                    if (!IsDisposed && !Disposing) cloudEvidence.Text = verified;
                    return;
                }
                string status=await Task.Run(()=>{
                    try
                    {
                        var data=OperationsApi.Request("GET","api/v1/mavlink/paths");
                        var record=new JavaScriptSerializer()
                            .Deserialize<Dictionary<string,object>>(data);
                        object rawPaths;
                        if(!record.TryGetValue("paths",out rawPaths))
                            return "Cloud status: path data unavailable";
                        var paths=rawPaths as object[];
                        if(paths==null || paths.Length==0)
                            return "Cloud status: path data unavailable";
                        var first=paths[0] as Dictionary<string,object>;
                        if(first==null)return "Cloud status: path data unavailable";
                        object value;
                        return "Cloud status: "+Convert.ToString(
                            first.TryGetValue("status",out value)?value:"unknown");
                    }
                    catch(WebException ex)
                    {
                        var response=ex.Response as HttpWebResponse;
                        bool legacy=response!=null &&
                            response.StatusCode==HttpStatusCode.NotFound;
                        if(response!=null)response.Dispose();
                        if(!legacy)throw;
                        // M1 has a healthy legacy telemetry endpoint; its
                        // readiness does not prove a fresh ATTITUDE stream.
                        var data=OperationsApi.Request("GET","api/v1/mavlink/health");
                        var health=new JavaScriptSerializer()
                            .Deserialize<Dictionary<string,object>>(data);
                        object online,fresh;
                        bool device=health.TryGetValue("device_online",out online) &&
                            online is bool && (bool)online;
                        bool current=health.TryGetValue("fresh",out fresh) &&
                            fresh is bool && (bool)fresh;
                        return "Cloud status: M1 "+(device&&current?"receiving telemetry":
                            "not ready")+"  |  Attitude not verified";
                    }
                });
                if(!IsDisposed && !Disposing) cloudEvidence.Text=status;
            }
            catch(WebException ex)
            {
                var response=ex.Response as HttpWebResponse;
                var auth=response!=null &&
                    (response.StatusCode==HttpStatusCode.Unauthorized ||
                     response.StatusCode==HttpStatusCode.Forbidden);
                if(response!=null)response.Dispose();
                if(!IsDisposed&&!Disposing)
                    cloudEvidence.Text=auth?"Cloud status: sign in required":
                        "Cloud status: unavailable";
            }
            catch(InvalidOperationException)
            {
                if(!IsDisposed&&!Disposing)
                    cloudEvidence.Text="Cloud status: sign in required";
            }
            catch(Exception)
            {
                if(!IsDisposed&&!Disposing)
                    cloudEvidence.Text="Cloud status: unavailable";
            }
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
                if (!VihangaLinkLoginWindow.EnsureSignedIn(main))
                    return false;
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

            if (!VihangaLinkLoginWindow.EnsureSignedIn(main))
                return false;
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
                    "AeroHelm — missing real heartbeat",
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
                    "AeroHelm — active native connection",
                    MessageBoxButtons.OK,MessageBoxIcon.Information);
                return false;
            }

            main.MenuFlightData.PerformClick();
            try
            {
                // MainV2.doConnect maps read-only TCP to 127.0.0.1:5760,
                // explicitly skips native PARAM_REQUEST_* and reuses the
                // existing Mission Planner HUD/map MAVLink parser.
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
                    "AeroHelm — cloud HUD not verified",
                    MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return false;
            }
        }
    }

}
