// Independent observational cloud attitude indicator. NOT a flight instrument.
// Uses only genuine ATTITUDE (30) packets decoded by the private M1 backend.
// Does not synthesize MAVLink or send any FC/vehicle commands.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using Drawing2D = FrameworkDrawing::System.Drawing.Drawing2D;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmCloudAttitudeWindow : Form
    {
        private readonly AttitudeSurface canvas = new AttitudeSurface();
        private readonly Label info = new Label();
        private readonly Timer poll = new Timer();
        private bool busy;
        private DateTime lastRefreshUtc = DateTime.MinValue;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        internal AeroHelmCloudAttitudeWindow()
        {
            Text="AeroHelm — Cloud attitude OBSERVATION ONLY";
            Width=820; Height=650;
            MinimumSize=new Drawing.Size(570,470);
            StartPosition=FormStartPosition.CenterParent;
            AutoScaleMode=AutoScaleMode.Dpi;
            BackColor=Drawing.Color.FromArgb(9,9,9);
            ForeColor=Drawing.Color.White;
            Font=new Drawing.Font("Segoe UI",11f);
            var header=new Label {
                Dock=DockStyle.Top,Height=70,
                Text="CLOUD ATTITUDE / REAL MAVLink ID 30 ONLY"+
                    Environment.NewLine+
                    "Not a certified flight instrument. Offline/stale values are hidden.",
                Font=new Drawing.Font("Segoe UI Semibold",11f),
                TextAlign=Drawing.ContentAlignment.MiddleCenter,
                ForeColor=Drawing.Color.FromArgb(220,220,220)
            };
            info.Dock=DockStyle.Bottom;info.Height=66;
            info.TextAlign=Drawing.ContentAlignment.MiddleCenter;
            info.Text="Awaiting authenticated real ATTITUDE; no synthetic horizon.";
            info.ForeColor=Drawing.Color.FromArgb(236,187,121);
            canvas.Dock=DockStyle.Fill;
            Controls.Add(canvas);
            Controls.Add(header);
            Controls.Add(info);
            poll.Interval=800;
            poll.Tick+=async(s,e)=>{
                if((DateTime.UtcNow-lastRefreshUtc).TotalSeconds>3)
                    canvas.Stale();
                await RefreshAttitudeAsync();
            };
            Shown+=async(s,e)=>{
                poll.Start();
                await RefreshAttitudeAsync();
            };
            FormClosed+=(s,e)=>poll.Stop();
        }

        private async Task RefreshAttitudeAsync()
        {
            if(busy||IsDisposed||Disposing)return;
            busy=true;
            try
            {
                string payload=await Task.Run(()=>OperationsApi.Request(
                    "GET","api/v1/mavlink/attitude"));
                if(IsDisposed||Disposing)return;
                var data=json.Deserialize<Dictionary<string,object>>(payload);
                object f,r,p,age;
                if(!data.TryGetValue("fresh",out f) || !(f is bool && (bool)f) ||
                   !data.TryGetValue("roll_deg",out r) ||
                   !data.TryGetValue("pitch_deg",out p) ||
                   r==null || p==null)
                {
                    canvas.Stale();
                    info.Text="NO FRESH ATTITUDE — native HUD must not use old roll/pitch.";
                    return;
                }
                float roll=Convert.ToSingle(r,CultureInfo.InvariantCulture);
                float pitch=Convert.ToSingle(p,CultureInfo.InvariantCulture);
                if(float.IsNaN(roll)||float.IsNaN(pitch)||
                   float.IsInfinity(roll)||float.IsInfinity(pitch)||
                   Math.Abs(roll)>180||Math.Abs(pitch)>90)
                {
                    canvas.Stale();info.Text="REJECTED NONFINITE/INVALID ATTITUDE";return;
                }
                lastRefreshUtc=DateTime.UtcNow;
                canvas.UpdateAngles(roll,pitch);
                data.TryGetValue("sample_age_ms",out age);
                info.Text="OBSERVED / roll "+roll.ToString("F1",CultureInfo.InvariantCulture)+
                    "°  pitch "+pitch.ToString("F1",CultureInfo.InvariantCulture)+
                    "°  age "+Convert.ToString(age,CultureInfo.InvariantCulture)+" ms"+
                    Environment.NewLine+
                    "Cloud-derived angles; physical aircraft and FC state not certified.";
            }
            catch(Exception)
            {
                if(!IsDisposed&&!Disposing)
                {
                    canvas.Stale();
                    info.Text="Cloud preview unavailable or SSO expired. "+
                        "Upgrade Oracle M1 or use the independent pilot radio.";
                }
            }
            finally { busy=false; }
        }

        private sealed class AttitudeSurface : Panel
        {
            private bool live;
            private float rollDeg,pitchDeg;
            internal AttitudeSurface()
            {
                BackColor=Drawing.Color.FromArgb(13,13,13);
                DoubleBuffered=true;
                ResizeRedraw=true;
            }
            internal void UpdateAngles(float roll,float pitch)
            {
                rollDeg=roll;pitchDeg=pitch;live=true;Invalidate();
            }
            internal void Stale()
            {
                if(live){live=false;Invalidate();}
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g=e.Graphics;
                g.SmoothingMode=Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(BackColor);
                int diameter=Math.Max(100,Math.Min(Width,Height)-36);
                var area=new Drawing.Rectangle(
                    (Width-diameter)/2,(Height-diameter)/2,diameter,diameter);
                if(!live)
                {
                    using(var textBrush=new Drawing.SolidBrush(Drawing.Color.FromArgb(236,187,121)))
                    using(var font=new Drawing.Font("Segoe UI Semibold",13f))
                        g.DrawString("NO LIVE ATTITUDE — STALE OR UNAVAILABLE",
                            font,textBrush,
                            new Drawing.RectangleF(0,Height/2f-30,Width,70),
                            new Drawing.StringFormat {
                                Alignment=Drawing.StringAlignment.Center,
                                LineAlignment=Drawing.StringAlignment.Center
                            });
                    return;
                }
                using(var mask=new Drawing2D.GraphicsPath())
                using(var sky=new Drawing.SolidBrush(Drawing.Color.FromArgb(43,115,171)))
                using(var ground=new Drawing.SolidBrush(Drawing.Color.FromArgb(133,96,64)))
                using(var white=new Drawing.Pen(Drawing.Color.White,3))
                using(var rim=new Drawing.Pen(Drawing.Color.FromArgb(225,225,225),3))
                {
                    mask.AddEllipse(area);
                    g.SetClip(mask);
                    g.TranslateTransform(Width/2f,Height/2f);
                    g.RotateTransform(-rollDeg);
                    float offset=pitchDeg*(diameter/90f);
                    float far=diameter*2;
                    g.FillRectangle(sky,-far,-far,far*2,far+offset);
                    g.FillRectangle(ground,-far,offset,far*2,far);
                    g.DrawLine(white,-far,offset,far,offset);
                    g.ResetTransform();
                    g.ResetClip();
                    g.DrawEllipse(rim,area);
                    // Aircraft-reference reticle remains stationary.
                    g.DrawLine(white,Width/2f-40,Height/2f,Width/2f-10,Height/2f);
                    g.DrawLine(white,Width/2f+10,Height/2f,Width/2f+40,Height/2f);
                    g.DrawLine(white,Width/2f,Height/2f-12,Width/2f,Height/2f+10);
                }
            }
        }
    }
}
