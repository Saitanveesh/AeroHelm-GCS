// AeroHelm Touch Launcher — genuine Windows Mission Planner navigation.
// Nine large touch targets, backed by native MP Flight Data/Flight Planner
// and existing AeroHelm plugins. Mobile is a separate future release.
// Copyright AeroHelm contributors, GPL-3.0-or-later.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using Drawing2D = FrameworkDrawing::System.Drawing.Drawing2D;
using MissionPlanner;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    // Windows-only nine-workspace shell. Keep native Mission Planner in charge
    // of the aircraft; this launcher only navigates to existing UI windows.
    internal sealed class AeroHelmTouchPanel : Form
    {
        private readonly TableLayoutPanel tiles = new TableLayoutPanel();
        private readonly Label linkStatus = new Label();
        private readonly Button refreshStatus = new Button();
        private readonly Label liveBridgeStatus = new Label();
        private readonly System.Windows.Forms.Timer bridgeStatusTimer =
            new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer cloudHealthTimer =
            new System.Windows.Forms.Timer();
        private bool opening;
        private bool refreshingStatus;

        private static readonly string[] Names = {
            "FLIGHT", "PLAN", "VISION",
            "OPS", "SECURITY", "FLEET",
            "LINK", "ANALYTICS", "SETTINGS"
        };

        public AeroHelmTouchPanel()
        {
            Text = "AeroHelm — Windows Control Workspace (0.2.0 RC / " +
                AeroHelmReleaseIdentity.BuildId + ")";
            Width = 1100;
            Height = 800;
            MinimumSize = new Drawing.Size(640, 540);
            WindowState = FormWindowState.Maximized;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Drawing.Color.FromArgb(7, 7, 7);
            ForeColor = Drawing.Color.White;
            Font = new Drawing.Font("Segoe UI", 11f);
            KeyPreview = true;

            // A restrained exit route is essential: close HOME to reveal the
            // unmodified native Mission Planner controls and recovery UI.
            var top = new TableLayoutPanel {
                Dock = DockStyle.Top, Height = 64, ColumnCount = 3,
                BackColor = BackColor, Padding = new Padding(14, 8, 14, 5)
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var back = new Button {
                Text = "← MISSION PLANNER", Dock = DockStyle.Fill,
                BackColor = Drawing.Color.FromArgb(36,36,36),
                ForeColor = Drawing.Color.White, FlatStyle = FlatStyle.Flat
            };
            back.FlatAppearance.BorderSize = 0;
            back.Click += (s, e) => Close();
            var heading = new Label {
                Text = "AEROHELM", Dock = DockStyle.Fill,
                TextAlign = Drawing.ContentAlignment.MiddleCenter,
                Font = new Drawing.Font("Segoe UI Semibold", 18f, Drawing.FontStyle.Bold)
            };
            var close = new Button {
                Text = "CLOSE  ×", Dock = DockStyle.Fill,
                BackColor = Drawing.Color.FromArgb(36,36,36),
                ForeColor = Drawing.Color.White, FlatStyle = FlatStyle.Flat
            };
            close.FlatAppearance.BorderSize = 0;
            close.Click += (s, e) => Close();
            top.Controls.Add(back, 0, 0);
            top.Controls.Add(heading, 1, 0);
            top.Controls.Add(close, 2, 0);

            liveBridgeStatus.Text = "LOCAL TCP / UNVERIFIED — NO PHYSICAL FLIGHT AUTHORITY";
            liveBridgeStatus.Dock = DockStyle.Bottom;
            liveBridgeStatus.Height = 32;
            liveBridgeStatus.TextAlign = Drawing.ContentAlignment.MiddleCenter;
            liveBridgeStatus.AutoEllipsis = true;
            liveBridgeStatus.ForeColor = Drawing.Color.FromArgb(185,185,185);
            liveBridgeStatus.Font = new Drawing.Font("Segoe UI", 9f);
            bridgeStatusTimer.Interval = 2000;
            bridgeStatusTimer.Tick += (s,e) => {
                var bridge = AeroHelmCloudPlugin.ActiveNativeBridge;
                liveBridgeStatus.Text = bridge == null ?
                    "LOCAL TCP / EXTERNAL OR DISABLED — NO PHYSICAL FLIGHT AUTHORITY" :
                    bridge.Summary + "  |  READ ONLY";
            };
            bridgeStatusTimer.Start();
            // Keep stale data stale: refresh the cloud's read-only observations
            // every five seconds, even when the operator never presses refresh.
            cloudHealthTimer.Interval = 5000;
            cloudHealthTimer.Tick += async (s,e) => await RefreshCloudStatusAsync();

            var linkBar = new TableLayoutPanel {
                Dock = DockStyle.Bottom, Height = 48, ColumnCount = 2,
                BackColor = Drawing.Color.FromArgb(14,14,14),
                Padding = new Padding(16,4,16,4)
            };
            linkBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,76));
            linkBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,24));
            linkStatus.Dock = DockStyle.Fill;
            linkStatus.Text = "CLOUD STATUS / UNKNOWN — no verified telemetry";
            linkStatus.ForeColor = Drawing.Color.FromArgb(185,185,185);
            linkStatus.Font = new Drawing.Font("Segoe UI Semibold",10f,Drawing.FontStyle.Bold);
            linkStatus.TextAlign = Drawing.ContentAlignment.MiddleLeft;
            linkStatus.AutoEllipsis = true;
            refreshStatus.Text = "REFRESH LINK";
            refreshStatus.Dock = DockStyle.Fill;
            refreshStatus.FlatStyle = FlatStyle.Flat;
            refreshStatus.FlatAppearance.BorderSize = 0;
            refreshStatus.BackColor = Drawing.Color.FromArgb(36,36,36);
            refreshStatus.ForeColor = Drawing.Color.White;
            refreshStatus.Click += async (s,e) => await RefreshCloudStatusAsync();
            linkBar.Controls.Add(linkStatus,0,0);
            linkBar.Controls.Add(refreshStatus,1,0);

            // Deliberate fixed 3x3 geometry, even after resizing. FlowLayoutPanel
            // could wrap the last tile into a fourth row, violating the reference.
            tiles.Dock = DockStyle.Fill;
            tiles.ColumnCount = 3;
            tiles.RowCount = 3;
            tiles.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
            tiles.Padding = new Padding(9);
            tiles.BackColor = BackColor;
            for (int i = 0; i < 3; i++)
            {
                tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
                tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
            }
            for (int i = 0; i < Names.Length; i++)
            {
                var name = Names[i];
                var tile = new AeroHelmTileButton(name) {
                    Dock = DockStyle.Fill, Margin = new Padding(5)
                };
                tile.Click += (sender, args) => Activate(name);
                tiles.Controls.Add(tile, i % 3, i / 3);
            }
            Controls.Add(tiles);
            Controls.Add(liveBridgeStatus);
            Controls.Add(linkBar);
            Controls.Add(top);
            Shown += async (s,e) => {
                cloudHealthTimer.Start();
                await RefreshCloudStatusAsync();
            };
            KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            };
            FormClosed += (s,e) => {
                bridgeStatusTimer.Stop();
                cloudHealthTimer.Stop();
                bridgeStatusTimer.Dispose();
                cloudHealthTimer.Dispose();
            };
        }

        // This is a deliberately bounded observational GET, not a flight
        // command or automatic remote control connection. Authentication and
        // failure states are visible; no last-known-good state is reused.
        private async Task RefreshCloudStatusAsync()
        {
            if (refreshingStatus || IsDisposed || Disposing) return;
            refreshingStatus = true;
            refreshStatus.Enabled = false;
            linkStatus.Text = "CLOUD STATUS / CHECKING — NO FLIGHT AUTHORITY";
            linkStatus.ForeColor = Drawing.Color.FromArgb(185,185,185);
            try
            {
                var raw = await Task.Run(() => OperationsApi.Request(
                    "GET","api/v1/mavlink/health"));
                if (IsDisposed || Disposing) return;
                var result = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string,object>>(raw);
                object freshValue, onlineValue, ageValue, idValue;
                var fresh = result.TryGetValue("fresh",out freshValue) &&
                            freshValue is bool && (bool)freshValue;
                var connected = result.TryGetValue("device_online",out onlineValue) &&
                                onlineValue is bool && (bool)onlineValue;
                result.TryGetValue("last_frame_age_ms",out ageValue);
                result.TryGetValue("vehicle_id",out idValue);
                string age = ageValue == null ? "UNKNOWN" :
                    Convert.ToString(ageValue,System.Globalization.CultureInfo.InvariantCulture);
                string aircraft = idValue == null ? "UNKNOWN" :
                    Convert.ToString(idValue,System.Globalization.CultureInfo.InvariantCulture);
                // New backend distinguishes observed HEARTBEAT and ATTITUDE.
                // Old M1 releases might omit these fields: UNKNOWN, not PASS.
                object hbValue,attValue,hbAgeValue;
                bool? heartbeatLive = null;
                bool? attitudeLive = null;
                if (result.TryGetValue("native_hud_heartbeat_ready",out hbValue)
                    && hbValue is bool)
                    heartbeatLive = (bool)hbValue;
                else if (result.TryGetValue("heartbeat_age_ms",out hbAgeValue)
                         && hbAgeValue != null)
                {
                    double hbAge;
                    if (double.TryParse(Convert.ToString(hbAgeValue,
                        System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,out hbAge))
                        heartbeatLive = hbAge >= 0 && hbAge < 5000;
                }
                if (result.TryGetValue("attitude_stream_active",out attValue)
                    && attValue is bool)
                    attitudeLive = (bool)attValue;

                if (fresh && connected)
                {
                    if (heartbeatLive == false)
                    {
                        linkStatus.Text = "CLOUD / NO LIVE HEARTBEAT — " + aircraft +
                            "  |  CHECK PI UART / FC POWER / STREAM";
                        linkStatus.ForeColor = Drawing.Color.FromArgb(244,177,150);
                    }
                    else if (attitudeLive == false)
                    {
                        linkStatus.Text = "CLOUD / ATTITUDE STREAM MISSING — " + aircraft +
                            "  |  NATIVE HORIZON CANNOT UPDATE";
                        linkStatus.ForeColor = Drawing.Color.FromArgb(244,177,150);
                    }
                    else if (heartbeatLive == true && attitudeLive == true)
                    {
                        linkStatus.Text = "CLOUD / HEARTBEAT + ATTITUDE OBSERVED — " +
                            aircraft + "  |  " + age +
                            " ms  |  CHECK PHYSICAL HUD MOVEMENT";
                        linkStatus.ForeColor = Drawing.Color.FromArgb(197,231,197);
                    }
                    else
                    {
                        linkStatus.Text = "CLOUD / FRAMES PRESENT, HUD UNVERIFIED — " +
                            aircraft + "  |  LEGACY BACKEND";
                        linkStatus.ForeColor = Drawing.Color.FromArgb(244,210,161);
                    }
                }
                else
                {
                    linkStatus.Text = "CLOUD / STALE OR DISCONNECTED — " + aircraft +
                        "  |  DO NOT RELY ON OLD TELEMETRY";
                    linkStatus.ForeColor = Drawing.Color.FromArgb(244,177,150);
                }
            }
            catch (Exception ex)
            {
                if (IsDisposed || Disposing) return;
                bool auth = ex is InvalidOperationException &&
                    ex.Message.IndexOf("login",StringComparison.OrdinalIgnoreCase) >= 0;
                linkStatus.Text = auth ?
                    "CLOUD / SIGN IN USING CLOUD — NO VERIFIED TELEMETRY" :
                    "CLOUD / UNAVAILABLE — NO VERIFIED TELEMETRY";
                linkStatus.ForeColor = Drawing.Color.FromArgb(244,177,150);
            }
            finally
            {
                if (!IsDisposed && !Disposing && AeroHelmCloudPlugin.ActiveNativeBridge != null)
                    linkStatus.Text += "  |  " + AeroHelmCloudPlugin.ActiveNativeBridge.Summary;
                refreshingStatus = false;
                if (!IsDisposed && !Disposing) refreshStatus.Enabled = true;
            }
        }

        private void Activate(string selected)
        {
            if (opening) return;
            opening = true;
            try
            {
                switch (selected)
                {
                    case "FLIGHT":
                        OpenNative(main => {
                            main.MenuFlightData.PerformClick();
                            // One-click private native HUD: NO repeated TCP/IP prompts.
                            // Normal MAVLink heartbeat checks still run, and all
                            // outbound GCS packets are discarded by the bridge.
                            var bridge = AeroHelmCloudPlugin.ActiveNativeBridge;
                            if (bridge != null && bridge.IsListening &&
                                string.Equals(Environment.GetEnvironmentVariable(
                                    "AEROHELM_AUTO_NATIVE_TCP"),"1",
                                    StringComparison.Ordinal) &&
                                !MainV2.comPort.BaseStream.IsOpen)
                            {
                                try
                                {
                                    main.doConnect(MainV2.comPort,"TCP","57600",
                                                   false,false);
                                }
                                catch (Exception ex)
                                {
                                    MessageBox.Show(main,
                                        "Native read-only TCP connection failed. " +
                                        "Verify a real Pixhawk HEARTBEAT is arriving " +
                                        "before treating Flight Data as live." +
                                        Environment.NewLine+ex.Message,
                                        "AeroHelm — live HUD not verified",
                                        MessageBoxButtons.OK,MessageBoxIcon.Warning);
                                }
                            }
                        });
                        break;
                    case "PLAN":
                        OpenNative(main => main.MenuFlightPlanner.PerformClick());
                        break;
                    case "VISION":
                        OpenChild(new AeroHelmVisionWindow());
                        break;
                    case "OPS":
                        OpenChild(new AeroHelmOpsWindow());
                        break;
                    case "SECURITY":
                        OpenChild(new AeroHelmReadOnlyPage("SECURITY"));
                        break;
                    case "FLEET":
                        OpenChild(new AeroHelmReadOnlyPage("FLEET"));
                        break;
                    case "LINK":
                        OpenChild(new AeroHelmLinkWindow());
                        break;
                    case "ANALYTICS":
                        OpenChild(new AeroHelmReadOnlyPage("ANALYTICS"));
                        break;
                    case "SETTINGS":
                        OpenChild(new AeroHelmTouchSettingsWindow());
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Unable to open " + selected + ": " + ex.Message,
                    "AeroHelm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { opening = false; }
        }

        private void OpenNative(Action<MainV2> action)
        {
            var main = MainV2.instance;
            if (main == null || main.IsDisposed)
                throw new InvalidOperationException("Mission Planner is unavailable.");
            Close();
            main.Activate();
            action(main);
        }

        private void OpenChild(Form form)
        {
            using (form) form.ShowDialog(this);
        }
    }

    // The bitmap artwork is generated from line geometry (not font glyphs),
    // ensuring the supplied monochrome reference has stable Windows rendering.
    internal sealed class AeroHelmTileButton : Button
    {
        private readonly Drawing.Bitmap icon;
        private bool hovered;
        private bool pressed;

        public AeroHelmTileButton(string name)
        {
            Text = name;
            AccessibleName = name + " AeroHelm workspace";
            AccessibleDescription = "Open the " + name + " module";
            BackColor = Drawing.Color.FromArgb(7,7,7);
            ForeColor = Drawing.Color.FromArgb(248,248,248);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            TabStop = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            icon = AeroHelmIconPainter.Make(name, 72);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true; Invalidate(); base.OnMouseEnter(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = true; Invalidate(); base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false; Invalidate(); base.OnMouseUp(e);
        }
        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e); Invalidate();
        }
        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e); Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.FromArgb(7,7,7));
            if (Width < 12 || Height < 12) return;
            float radius = Math.Min(19f, Math.Min(Width,Height) / 7f);
            using (var path = RoundedRectangle(1,1,Width-3,Height-3,radius))
            {
                var shade = pressed ? Drawing.Color.FromArgb(67,67,67) :
                            hovered ? Drawing.Color.FromArgb(53,53,53) :
                                      Drawing.Color.FromArgb(37,37,37);
                using (var brush = new Drawing.SolidBrush(shade)) g.FillPath(brush,path);
                if (Focused && ShowFocusCues)
                {
                    using (var focus = new Drawing.Pen(Drawing.Color.FromArgb(225,225,225),2f))
                        g.DrawPath(focus,path);
                }
            }
            int iconSize = Math.Max(22, Math.Min(52, Height / 3));
            int textSize = Math.Max(10, Math.Min(18, Height / 14));
            int gap = Math.Max(5, Math.Min(15, Height / 12));
            int labelHeight = textSize + 11;
            int blockTop = (Height - (iconSize + gap + labelHeight)) / 2;
            g.DrawImage(icon, (Width-iconSize)/2, blockTop, iconSize, iconSize);
            using (var font = new Drawing.Font("Segoe UI Semibold",textSize,
                       Drawing.FontStyle.Bold,Drawing.GraphicsUnit.Pixel))
            using (var brush = new Drawing.SolidBrush(ForeColor))
            using (var format = new Drawing.StringFormat {
                       Alignment = Drawing.StringAlignment.Center,
                       LineAlignment = Drawing.StringAlignment.Center })
                g.DrawString(Text,font,brush,
                    new Drawing.RectangleF(7,blockTop+iconSize+gap,Width-14,labelHeight),format);
        }

        private static Drawing2D.GraphicsPath RoundedRectangle(
            float x,float y,float w,float h,float r)
        {
            var path = new Drawing2D.GraphicsPath();
            float d = 2f*r;
            path.AddArc(x,y,d,d,180,90);
            path.AddArc(x+w-d,y,d,d,270,90);
            path.AddArc(x+w-d,y+h-d,d,d,0,90);
            path.AddArc(x,y+h-d,d,d,90,90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) icon.Dispose();
            base.Dispose(disposing);
        }
    }

    internal static class AeroHelmIconPainter
    {
        public static Drawing.Bitmap Make(string name, int size)
        {
            var bitmap = new Drawing.Bitmap(size,size);
            using (var g = Drawing.Graphics.FromImage(bitmap))
            using (var pen = new Drawing.Pen(Drawing.Color.White, 2.6f))
            {
                g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
                g.TranslateTransform(size / 24f, size / 24f);
                g.ScaleTransform(size / 24f, size / 24f);
                pen.StartCap = Drawing2D.LineCap.Round;
                pen.EndCap = Drawing2D.LineCap.Round;
                pen.LineJoin = Drawing2D.LineJoin.Round;
                switch(name)
                {
                    // Same vector icon system for touch and all toolbar plugins.
                    // No dependency on emoji glyphs, fonts or external images.
                    case "CLOUD":
                        g.DrawArc(pen,4,10,9,9,90,180);
                        g.DrawArc(pen,8,5,10,11,180,150);
                        g.DrawArc(pen,15,11,6,7,250,200);
                        g.DrawLine(pen,6,19,18,19);
                        break;
                    case "SURVEY":
                        Lines(g,pen,new[]{P(3,7),P(10,4),P(15,7),P(21,4),
                            P(21,18),P(15,21),P(10,18),P(3,21),P(3,7)});
                        g.DrawLine(pen,10,4,10,18);
                        g.DrawEllipse(pen,13,10,4,4);
                        break;
                    case "INTEL":
                        g.DrawEllipse(pen,7,7,10,10);
                        foreach(var point in new[]{P(12,2),P(21,8),P(21,16),
                            P(12,22),P(3,16),P(3,8)})
                        {
                            g.DrawEllipse(pen,point.X-1,point.Y-1,2,2);
                            g.DrawLine(pen,12,12,point.X,point.Y);
                        }
                        break;
                    case "TOUCH":
                        for(int row=0;row<3;row++)
                            for(int col=0;col<3;col++)
                                g.DrawRectangle(pen,3+col*7,3+row*7,4,4);
                        break;
                    case "FLIGHT":
                        Lines(g,pen, new [] {P(17,20),P(16,13),P(21,10),P(21,7),P(16,8),
                            P(12,2),P(10,3),P(13,10),P(8,14),P(4,13),P(3,15),P(8,18),
                            P(9,22),P(11,21),P(11,16),P(17,13)});
                        break;
                    case "PLAN":
                        Lines(g,pen,new[]{P(3,6),P(9,3),P(15,6),P(21,3),P(21,18),
                            P(15,21),P(9,18),P(3,21),P(3,6)});
                        g.DrawLine(pen,9,3,9,18);g.DrawLine(pen,15,6,15,21);break;
                    case "VISION":
                        g.DrawRoundedRectangleCompat(pen,3,5,14,14,3);
                        Lines(g,pen,new[]{P(17,10),P(22,6),P(22,18),P(17,14)});
                        break;
                    case "OPS":
                        foreach(var y in new[]{6,12,18})g.DrawLine(pen,4,y,20,y);
                        g.DrawLine(pen,9,3,9,9);g.DrawLine(pen,15,9,15,15);g.DrawLine(pen,9,15,9,21);
                        break;
                    case "SECURITY":
                        Lines(g,pen,new[]{P(12,2),P(20,5),P(20,11),P(18,17),
                            P(12,22),P(6,19),P(4,13),P(4,5),P(12,2)});
                        Lines(g,pen,new[]{P(9,12),P(11,14),P(15,9)});
                        break;
                    case "FLEET":
                        g.DrawRoundedRectangleCompat(pen,5,3,14,5,1);
                        g.DrawRoundedRectangleCompat(pen,4,10,16,5,1);
                        g.DrawRoundedRectangleCompat(pen,3,17,18,5,1);break;
                    case "LINK":
                        g.DrawEllipse(pen,9,2,6,6);
                        g.DrawEllipse(pen,1,17,6,6);
                        g.DrawEllipse(pen,17,17,6,6);
                        g.DrawLine(pen,12,8,12,13);
                        g.DrawLine(pen,3.9f,13,20,13);
                        g.DrawLine(pen,4,13,4,17);
                        g.DrawLine(pen,20,13,20,17);break;
                    case "ANALYTICS":
                        g.DrawLine(pen,3,20,3,13);g.DrawLine(pen,8,20,8,10);
                        g.DrawLine(pen,13,20,13,14);g.DrawLine(pen,18,20,18,6);
                        Lines(g,pen,new[]{P(3,11),P(8,6),P(13,9),P(21,2)});
                        break;
                    case "SETTINGS":
                        // Intentionally vector-drawn rather than relying on OS glyph fonts.
                        for(int i=0;i<8;i++){
                            double a=i*Math.PI/4;
                            float x1=(float)(12+9*Math.Cos(a)),y1=(float)(12+9*Math.Sin(a));
                            float x2=(float)(12+11*Math.Cos(a)),y2=(float)(12+11*Math.Sin(a));
                            g.DrawLine(pen,x1,y1,x2,y2);
                        }
                        g.DrawEllipse(pen,4,4,16,16);g.DrawEllipse(pen,9,9,6,6);break;
                }
            }
            return bitmap;
        }
        private static Drawing.PointF P(float x,float y)
        {
            return new Drawing.PointF(x,y);
        }
        private static void Lines(Drawing.Graphics g,Drawing.Pen pen,Drawing.PointF[] points)
        {
            g.DrawLines(pen,points);
        }
        // System.Drawing.Graphics has no native DrawRoundedRectangle in .NET 4.7.2.
        private static void DrawRoundedRectangleCompat(this Drawing.Graphics g,
            Drawing.Pen pen, float x,float y,float w,float h,float radius)
        {
            using(var path=new Drawing2D.GraphicsPath())
            {
                float d=radius*2;
                path.AddArc(x,y,d,d,180,90);
                path.AddArc(x+w-d,y,d,d,270,90);
                path.AddArc(x+w-d,y+h-d,d,d,0,90);
                path.AddArc(x,y+h-d,d,d,90,90);
                path.CloseFigure();
                g.DrawPath(pen,path);
            }
        }
    }

    internal static class AeroHelmReleaseIdentity
    {
        // The fingerprint comes from the ACTUAL installed DLL, not a branch
        // name or hardcoded UI label. It lets the operator confirm that the
        // latest plugin really loaded after replacing an old Mission Planner
        // release. No credentials or aircraft identifiers are exposed.
        internal static readonly string BuildId =
            typeof(AeroHelmCloudPlugin).Assembly.ManifestModule.ModuleVersionId
                .ToString("N").Substring(0,12);
        internal static string PluginSha256Prefix
        {
            get
            {
                try
                {
                    string path=typeof(AeroHelmCloudPlugin).Assembly.Location;
                    using(var input=System.IO.File.OpenRead(path))
                    using(var digest=System.Security.Cryptography.SHA256.Create())
                    {
                        byte[] bytes=digest.ComputeHash(input);
                        return BitConverter.ToString(bytes,0,8).Replace("-","")
                            .ToLowerInvariant();
                    }
                }
                catch(System.IO.IOException){ return "UNAVAILABLE"; }
                catch(UnauthorizedAccessException){ return "UNAVAILABLE"; }
            }
        }
    }

    // Windows-only settings hub. Does not expose bearer tokens or alter
    // aircraft configuration through the cloud.
    internal sealed class AeroHelmTouchSettingsWindow : Form
    {
        private readonly Label session = new Label();
        private readonly Label origin = new Label();
        private readonly Label build = new Label();

        public AeroHelmTouchSettingsWindow()
        {
            Text = "AeroHelm — Windows Settings";
            Width = 740; Height = 560;
            MinimumSize = new Drawing.Size(600,500);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Drawing.Color.FromArgb(8,8,8);
            ForeColor = Drawing.Color.White;
            Font = new Drawing.Font("Segoe UI",11);
            KeyPreview = true;

            var root = new TableLayoutPanel {
                Dock = DockStyle.Fill, Padding = new Padding(20),
                ColumnCount = 1, RowCount = 4
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,65));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,148));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,66));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(root);
            var heading = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1
            };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,75));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
            heading.Controls.Add(new Label {
                Text="SETTINGS  /  WINDOWS GCS",
                Dock=DockStyle.Fill,TextAlign=Drawing.ContentAlignment.MiddleLeft,
                Font=new Drawing.Font("Segoe UI Semibold",19,Drawing.FontStyle.Bold),
                ForeColor=Drawing.Color.White
            },0,0);
            var home = new Button {
                Text="← HOME", Dock=DockStyle.Fill, FlatStyle=FlatStyle.Flat,
                ForeColor=Drawing.Color.White,
                BackColor=Drawing.Color.FromArgb(37,37,37)
            };
            home.FlatAppearance.BorderSize=0;
            home.Click+=(s,e)=>Close();
            heading.Controls.Add(home,1,0);
            root.Controls.Add(heading,0,0);

            var info = new TableLayoutPanel { Dock=DockStyle.Fill,
                BackColor=Drawing.Color.FromArgb(33,33,33),
                ColumnCount=1,RowCount=3,Padding=new Padding(15,8,15,8) };
            info.RowStyles.Add(new RowStyle(SizeType.Percent,33));
            info.RowStyles.Add(new RowStyle(SizeType.Percent,33));
            info.RowStyles.Add(new RowStyle(SizeType.Percent,34));
            foreach(var label in new[]{session,origin,build})
            {
                label.Dock=DockStyle.Fill;
                label.ForeColor=Drawing.Color.FromArgb(228,228,228);
                label.TextAlign=Drawing.ContentAlignment.MiddleLeft;
                info.Controls.Add(label,0,info.Controls.Count);
            }
            root.Controls.Add(info,0,1);

            var actions = new FlowLayoutPanel {
                Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,
                WrapContents=false,AutoScroll=true,Padding=new Padding(0,15,0,0)
            };
            root.Controls.Add(actions,0,2);
            Add(actions,"CLOUD — login and vehicle registry",()=>{
                using(var window=new CloudWindow()) window.ShowDialog(this);
                RefreshSummary();
            });
            Add(actions,"LINK — connectivity diagnostics",()=>{
                using(var window=new AeroHelmLinkWindow()) window.ShowDialog(this);
            });
            Add(actions,"PARAMETERS — inspect verified read-only cache",()=>{
                using(var window=new AeroHelmIntelWindow("PARAMETERS"))
                    window.ShowDialog(this);
            });
            Add(actions,"MISSION PLANNER — native Config/Tuning",()=>{
                Close();
                var main=MainV2.instance;
                if(main!=null && !main.IsDisposed)
                {
                    main.Activate();
                    main.MenuConfigTune.PerformClick();
                }
            });
            Add(actions,"REFRESH — local session summary",RefreshSummary);
            root.Controls.Add(new Label {
                Text="Private cloud access is a view session, not flight-control authorization."+
                    Environment.NewLine+
                    "Native Mission Planner remains available for local recovery and configuration.",
                Dock=DockStyle.Fill,TextAlign=Drawing.ContentAlignment.MiddleLeft,
                ForeColor=Drawing.Color.FromArgb(180,180,180)
            },0,3);
            Shown+=(s,e)=>RefreshSummary();
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){Close();e.Handled=true;}};
        }

        private void RefreshSummary()
        {
            bool valid=!string.IsNullOrEmpty(Program.AeroHelmOperatorToken) &&
                DateTime.UtcNow<Program.AeroHelmTokenExpiresUtc;
            session.Text=valid ?
                "Operator cloud JWT: ACTIVE (expires "+Program.AeroHelmTokenExpiresUtc.ToString("u")+")" :
                "Operator cloud JWT: NOT AUTHENTICATED / EXPIRED";
            string configured=Program.AeroHelmCloudOrigin;
            if(string.IsNullOrWhiteSpace(configured))
                configured=AeroHelmWorkspaceSettings.CloudOrigin();
            origin.Text="Cloud origin: "+configured+
                "  (operator tokens are never shown)";
            build.Text="Loaded compiled AeroHelm plugin: "+AeroHelmReleaseIdentity.BuildId+
                " | DLL SHA-256: "+AeroHelmReleaseIdentity.PluginSha256Prefix;
            build.AccessibleName="AeroHelm installed plugin binary fingerprint";
        }

        private static void Add(FlowLayoutPanel host,string caption,Action action)
        {
            var button=new Button {
                Text=caption,Width=605,Height=49,FlatStyle=FlatStyle.Flat,
                BackColor=Drawing.Color.FromArgb(37,37,37),
                ForeColor=Drawing.Color.White,UseVisualStyleBackColor=false,
                TextAlign=Drawing.ContentAlignment.MiddleLeft,
                Padding=new Padding(16,0,0,0),Margin=new Padding(0,0,0,8)
            };
            button.FlatAppearance.BorderSize=0;
            button.Click+=(s,e)=>action();
            host.Controls.Add(button);
        }
    }

}
