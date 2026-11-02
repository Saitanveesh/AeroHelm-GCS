// AeroHelm Touch Launcher — genuine Windows Mission Planner navigation.
// Nine large touch targets, backed by native MP Flight Data/Flight Planner
// and existing AeroHelm plugins. Mobile is a separate future release.
// Copyright AeroHelm contributors, GPL-3.0-or-later.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using Drawing2D = FrameworkDrawing::System.Drawing.Drawing2D;
using MissionPlanner;
using System;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    // Windows-only nine-workspace shell. Keep native Mission Planner in charge
    // of the aircraft; this launcher only navigates to existing UI windows.
    internal sealed class AeroHelmTouchPanel : Form
    {
        private readonly TableLayoutPanel tiles = new TableLayoutPanel();
        private bool opening;

        private static readonly string[] Names = {
            "FLIGHT", "PLAN", "VISION",
            "OPS", "SECURITY", "FLEET",
            "LINK", "ANALYTICS", "SETTINGS"
        };

        public AeroHelmTouchPanel()
        {
            Text = "AeroHelm — Windows Control Workspace";
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

            var bottom = new Label {
                Text = "WINDOWS GCS  •  Native Mission Planner remains available  •  Cloud link does not authorize physical flight",
                Dock = DockStyle.Bottom, Height = 30,
                TextAlign = Drawing.ContentAlignment.MiddleCenter,
                ForeColor = Drawing.Color.FromArgb(155,155,155),
                Font = new Drawing.Font("Segoe UI", 9f)
            };

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
            Controls.Add(bottom);
            Controls.Add(top);
            KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            };
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
                        OpenNative(main => main.MenuFlightData.PerformClick());
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
            Add(stack,"Native Mission Planner configuration",()=>{
                Close();MainV2.instance.MenuConfigTune.PerformClick();
            });
            stack.Controls.Add(new Label {
                Text="Use CLOUD and native Mission Planner configuration for Windows operations. Cloud telemetry is read-only.",
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


    }
}
