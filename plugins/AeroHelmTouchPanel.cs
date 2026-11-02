// AeroHelm Touch Launcher — genuine Windows Mission Planner navigation.
// Nine large touch targets, backed by native MP Flight Data/Flight Planner
// and existing AeroHelm plugins. A phone uses the separate /touch/ PWA.
// Copyright AeroHelm contributors, GPL-3.0-or-later.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using Drawing2D = FrameworkDrawing::System.Drawing.Drawing2D;
using MissionPlanner;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmTouchPanel : Form
    {
        private readonly FlowLayoutPanel tiles = new FlowLayoutPanel();
        private readonly Label note = new Label();
        private readonly List<AeroHelmTileButton> buttons = new List<AeroHelmTileButton>();
        private int lastColumns;

        private static readonly string[] Names = {
            "FLIGHT", "PLAN", "VISION",
            "OPS", "SECURITY", "FLEET",
            "LINK", "ANALYTICS", "SETTINGS"
        };

        public AeroHelmTouchPanel()
        {
            Text = "AeroHelm — Touch Control Workspace";
            Width = 1050;
            Height = 820;
            MinimumSize = new Drawing.Size(470, 540);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Drawing.Color.FromArgb(7, 7, 7);
            ForeColor = Drawing.Color.White;
            Font = new Drawing.Font("Segoe UI", 12f);
            KeyPreview = true;

            var top = new Panel { Dock = DockStyle.Top, Height = 93,
                BackColor = Drawing.Color.FromArgb(7, 7, 7) };
            var brand = new Label {
                Text = "AEROHELM   /   TOUCH CONSOLE",
                Dock = DockStyle.Top, Height = 45,
                ForeColor = Drawing.Color.White,
                Font = new Drawing.Font("Segoe UI Semibold", 20f),
                Padding = new Padding(22, 12, 0, 0)
            };
            var subtitle = new Label {
                Text = "Nine workspaces. Mission Planner remains the native flight-control foundation.",
                Dock = DockStyle.Fill,
                ForeColor = Drawing.Color.FromArgb(173,173,173),
                Font = new Drawing.Font("Segoe UI", 10f),
                Padding = new Padding(24, 0, 0, 0)
            };
            top.Controls.Add(subtitle);
            top.Controls.Add(brand);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 42 };
            note.Text = "LOCAL GCS  |  Cloud telemetry is read-only  |  No hardware autonomous flight authorization";
            note.Dock = DockStyle.Fill;
            note.TextAlign = Drawing.ContentAlignment.MiddleCenter;
            note.Font = new Drawing.Font("Segoe UI", 9f);
            note.ForeColor = Drawing.Color.FromArgb(155,155,155);
            bottom.Controls.Add(note);

            tiles.Dock = DockStyle.Fill;
            tiles.AutoScroll = true;
            tiles.WrapContents = true;
            tiles.FlowDirection = FlowDirection.LeftToRight;
            tiles.Padding = new Padding(18, 14, 8, 18);
            tiles.BackColor = BackColor;

            Controls.Add(tiles);
            Controls.Add(bottom);
            Controls.Add(top);

            foreach (var name in Names)
            {
                var button = new AeroHelmTileButton(name);
                button.Click += (sender, args) => Activate(name);
                buttons.Add(button);
                tiles.Controls.Add(button);
            }
            tiles.ClientSizeChanged += (s, e) => LayoutTiles();
            Shown += (s, e) => LayoutTiles();
            KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Escape) Close();
            };
        }

        private void LayoutTiles()
        {
            if (IsDisposed || tiles.ClientSize.Width < 150) return;
            int cols = tiles.ClientSize.Width >= 760 ? 3 : 2;
            int usable = Math.Max(280, tiles.ClientSize.Width - tiles.Padding.Horizontal - 25);
            int width = Math.Max(130, usable / cols - 16);
            int height = cols == 3 ? 183 : 155;
            if (lastColumns != cols) lastColumns = cols;
            foreach (var button in buttons)
            {
                if (button.Width != width || button.Height != height)
                    button.Size = new Drawing.Size(width, height);
            }
        }

        private void Activate(string selected)
        {
            switch (selected)
            {
                case "FLIGHT":
                    Close();
                    MainV2.instance.MenuFlightData.PerformClick();
                    break;
                case "PLAN":
                    Close();
                    MainV2.instance.MenuFlightPlanner.PerformClick();
                    break;
                case "VISION":
                    OpenChild(new AeroHelmVisionWindow());
                    break;
                case "OPS":
                    OpenChild(new AeroHelmOpsWindow());
                    break;
                case "SECURITY":
                    OpenChild(new AeroHelmIntelWindow("SECURITY"));
                    break;
                case "FLEET":
                    OpenChild(new AeroHelmIntelWindow("FLEET"));
                    break;
                case "LINK":
                    OpenChild(new AeroHelmLinkWindow());
                    break;
                case "ANALYTICS":
                    OpenChild(new AeroHelmIntelWindow("ANALYTICS"));
                    break;
                case "SETTINGS":
                    OpenChild(new AeroHelmTouchSettingsWindow());
                    break;
            }
        }

        private void OpenChild(Form form)
        {
            using (form) form.ShowDialog(this);
        }
    }

    internal sealed class AeroHelmTileButton : Button
    {
        private readonly Drawing.Bitmap icon;
        public AeroHelmTileButton(string name)
        {
            Text = name;
            AccessibleName = name + " AeroHelm workspace";
            AccessibleDescription = "Open the " + name + " module";
            Height = 180; Width = 270;
            BackColor = Drawing.Color.FromArgb(36,36,36);
            ForeColor = Drawing.Color.White;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor = Drawing.Color.FromArgb(50,50,50);
            FlatAppearance.BorderSize = 1;
            FlatAppearance.MouseOverBackColor = Drawing.Color.FromArgb(52,52,52);
            FlatAppearance.MouseDownBackColor = Drawing.Color.FromArgb(67,67,67);
            Font = new Drawing.Font("Segoe UI", 20f, Drawing.FontStyle.Regular);
            Margin = new Padding(8);
            Padding = new Padding(8,12,8,12);
            TextAlign = Drawing.ContentAlignment.BottomCenter;
            ImageAlign = Drawing.ContentAlignment.MiddleCenter;
            TextImageRelation = TextImageRelation.ImageAboveText;
            UseVisualStyleBackColor = false;
            icon = AeroHelmIconPainter.Make(name, 58);
            Image = icon;
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

    internal sealed class AeroHelmTouchSettingsWindow : Form
    {
        public AeroHelmTouchSettingsWindow()
        {
            Text = "AeroHelm — Touch Settings";
            Width = 530; Height = 340;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Drawing.Color.FromArgb(22,22,22);
            ForeColor = Drawing.Color.White;
            var stack=new FlowLayoutPanel {
                Dock=DockStyle.Fill,Padding=new Padding(18),
                FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true
            };
            Controls.Add(stack);
            stack.Controls.Add(new Label {
                Text="Mission Planner for Windows remains the flight-control foundation.",
                Width=460,Height=52,ForeColor=Drawing.Color.White
            });
            Add(stack,"Cloud registry and operator session",()=> {
                using(var w=new CloudWindow())w.ShowDialog(this);
            });
            Add(stack,"Open private mobile touch console",OpenTouchUrl);
            Add(stack,"Native Mission Planner configuration",()=>{
                Close();MainV2.instance.MenuConfigTune.PerformClick();
            });
            stack.Controls.Add(new Label {
                Text="The phone console is read-only. It requires private HTTPS cloud access and a separate browser login.",
                Width=460,Height=66,ForeColor=Drawing.Color.LightGray
            });
        }

        private void Add(FlowLayoutPanel host,string title,Action action)
        {
            var b=new Button {
                Text=title,Width=450,Height=45,FlatStyle=FlatStyle.Flat,
                BackColor=Drawing.Color.FromArgb(45,45,45),ForeColor=Drawing.Color.White
            };
            b.Click+=(s,e)=>action();
            host.Controls.Add(b);
        }

        private void OpenTouchUrl()
        {
            try
            {
                var baseUrl=AeroHelmWorkspaceSettings.CheckedPrivateUrl(
                    AeroHelmWorkspaceSettings.CloudOrigin());
                if(baseUrl.Scheme!=Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Mobile portal requires HTTPS.");
                var origin=baseUrl.GetLeftPart(UriPartial.Authority);
                var touch=new Uri(origin+"/touch/");
                Process.Start(new ProcessStartInfo{FileName=touch.AbsoluteUri,UseShellExecute=true});
            }
            catch(Exception ex)
            {
                MessageBox.Show(this,"Unable to open private touch portal: "+ex.Message,
                    "AeroHelm",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
        }
    }
}
