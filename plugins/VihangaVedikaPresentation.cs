// Vihanga Vedika — native toolbar icons, installed-release identity and settings.
// The Mission Planner executable, plugin assembly name and persisted settings
// keys remain unchanged for compatibility with existing installations.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using Drawing2D = FrameworkDrawing::System.Drawing.Drawing2D;
using MissionPlanner;
using System;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal static class VihangaVedikaProduct
    {
        internal const string Name = "Vihanga Vedika";
        internal const string FullName = "Vihanga Vedika Ground Control Station";
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
                    // Vector artwork shared by the native toolbar.
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
                    case "PATHS":
                        // Two explicitly separate links, no auto-failover.
                        g.DrawEllipse(pen,2,3,7,7);
                        g.DrawEllipse(pen,15,14,7,7);
                        g.DrawLine(pen,7,8,17,17);
                        g.DrawLine(pen,17,17,13,17);
                        g.DrawLine(pen,17,17,17,13);
                        break;
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
    internal sealed class VihangaVedikaSettingsWindow : Form
    {
        private readonly Label session = new Label();
        private readonly Label origin = new Label();
        private readonly Label build = new Label();

        public VihangaVedikaSettingsWindow()
        {
            Text = "Vihanga Vedika — Ground Station Settings";
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
                Text="SETTINGS  /  VIHANGA VEDIKA",
                Dock=DockStyle.Fill,TextAlign=Drawing.ContentAlignment.MiddleLeft,
                Font=new Drawing.Font("Segoe UI Semibold",19,Drawing.FontStyle.Bold),
                ForeColor=Drawing.Color.White
            },0,0);
            var home = new Button {
                Text="CLOSE", Dock=DockStyle.Fill, FlatStyle=FlatStyle.Flat,
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
            build.Text="Loaded ground-station plugin: "+AeroHelmReleaseIdentity.BuildId+
                " | DLL SHA-256: "+AeroHelmReleaseIdentity.PluginSha256Prefix;
            build.AccessibleName="Ground-station plugin binary fingerprint";
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
