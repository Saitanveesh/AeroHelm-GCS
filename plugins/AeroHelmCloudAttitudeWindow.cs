// Passive cloud attitude monitor. Uses the same authenticated MAVLink WSS
// feed as the native GCS bridge, not an optional HTTP attitude-preview route.
// Only validated physical MAVLink ATTITUDE (message 30) updates the canvas.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using Drawing2D = FrameworkDrawing::System.Drawing.Drawing2D;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MissionPlanner;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmCloudAttitudeWindow : Form
    {
        private readonly AttitudeSurface canvas = new AttitudeSurface();
        private readonly Label info = new Label();
        private readonly Label header = new Label();
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly AeroHelmLinkQuality quality = new AeroHelmLinkQuality();
        private bool streamConnected;
        private string connectionIssue = "Waiting for authenticated cloud telemetry.";
        private long framesReceived;
        private long heartbeatsReceived;
        private long attitudesReceived;
        private long framesRejected;
        private readonly Dictionary<int, long> messageIds = new Dictionary<int, long>();
        private float lastRoll;
        private float lastPitch;

        internal AeroHelmCloudAttitudeWindow()
        {
            Text = "Vihanga GCS — Live cloud attitude";
            Width = 820; Height = 650;
            MinimumSize = new Drawing.Size(570, 470);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Drawing.Color.FromArgb(9, 9, 9);
            ForeColor = Drawing.Color.White;
            Font = new Drawing.Font("Segoe UI", 11f);
            header.Dock = DockStyle.Top;
            header.Height = 54;
            header.Text = "MAVLink attitude / authenticated cloud telemetry";
            header.Font = new Drawing.Font("Segoe UI Semibold", 11f);
            header.TextAlign = Drawing.ContentAlignment.MiddleCenter;
            header.ForeColor = Drawing.Color.FromArgb(220, 220, 220);
            info.Dock = DockStyle.Bottom;
            info.Height = 106;
            info.TextAlign = Drawing.ContentAlignment.MiddleCenter;
            info.Text = connectionIssue;
            info.ForeColor = Drawing.Color.FromArgb(236, 187, 121);
            canvas.Dock = DockStyle.Fill;
            Controls.Add(canvas);
            Controls.Add(header);
            Controls.Add(info);
            poll.Interval = 400;
            poll.Tick += (s, e) => RefreshDisplay();
            Shown += async (s, e) => await WatchAsync();
            FormClosed += (s, e) =>
            {
                poll.Stop();
                shutdown.Cancel();
            };
        }

        private void RefreshDisplay()
        {
            if (IsDisposed || Disposing) return;
            var metrics = quality.Snapshot();
            bool live = streamConnected && metrics.AttitudeFresh;
            if (live)
                canvas.UpdateAngles(lastRoll, lastPitch);
            else
                canvas.Stale();

            string counts = "MAVLink frames " + framesReceived +
                "   HB " + heartbeatsReceived +
                "   ATT " + attitudesReceived +
                "   rejected " + framesRejected +
                "   other IDs " + OtherMessageIdSummary();
            if (live)
            {
                info.Text = "LIVE / roll " + lastRoll.ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture) +
                    "°   pitch " + lastPitch.ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture) +
                    "°   age " + metrics.AttitudeAgeMs + " ms" +
                    Environment.NewLine + counts;
            }
            else if (streamConnected)
            {
                info.Text = (heartbeatsReceived > 0 ?
                    "HEARTBEAT RECEIVED; ATTITUDE (ID 30) NOT FRESH." :
                    "Connected; waiting for genuine MAVLink HEARTBEAT and ATTITUDE.") +
                    Environment.NewLine + counts +
                    Environment.NewLine + "ID 30 not received: inspect FC-to-Pi stream rates, then M1 relay.";
            }
            else
            {
                info.Text = connectionIssue + Environment.NewLine + counts;
            }
        }

        private string OtherMessageIdSummary()
        {
            var keys = new List<int>(messageIds.Keys);
            keys.Sort();
            var fragments = new List<string>();
            foreach (int id in keys)
            {
                if (id == 0 || id == 30) continue;
                fragments.Add(id + ":" + messageIds[id]);
                if (fragments.Count == 6) break;
            }
            return fragments.Count == 0 ? "none" : string.Join(",", fragments);
        }

        private async Task WatchAsync()
        {
            poll.Start();
            while (!shutdown.IsCancellationRequested)
            {
                if (string.IsNullOrWhiteSpace(Program.AeroHelmOperatorToken) ||
                    DateTime.UtcNow >= Program.AeroHelmTokenExpiresUtc)
                {
                    connectionIssue = "Cloud session expired or not authenticated. Sign in again.";
                    streamConnected = false;
                    quality.Reset();
                    RefreshDisplay();
                    break;
                }
                streamConnected = false;
                quality.Reset();
                canvas.Stale();
                try
                {
                    var url = AeroHelmReceiveOnlyBridge.PrivateWebSocketUri();
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        shutdown.Token))
                    using (var ws = new ClientWebSocket())
                    {
                        var lifetime = Program.AeroHelmTokenExpiresUtc - DateTime.UtcNow;
                        if (lifetime <= TimeSpan.Zero) break;
                        linked.CancelAfter(lifetime);
                        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                        ws.Options.SetRequestHeader("Authorization",
                            "Bearer " + Program.AeroHelmOperatorToken);
                        connectionIssue = "Connecting to authenticated MAVLink WSS viewer.";
                        RefreshDisplay();
                        await ws.ConnectAsync(url, linked.Token);
                        streamConnected = true;
                        framesReceived = heartbeatsReceived = attitudesReceived =
                            framesRejected = 0;
                        messageIds.Clear();
                        RefreshDisplay();

                        var packet = new byte[512];
                        while (!linked.IsCancellationRequested &&
                               ws.State == WebSocketState.Open)
                        {
                            int length = 0;
                            WebSocketReceiveResult part;
                            do
                            {
                                if (length == packet.Length)
                                    throw new InvalidDataException(
                                        "Cloud WSS packet exceeds MAVLink frame limit.");
                                part = await ws.ReceiveAsync(new ArraySegment<byte>(
                                    packet, length, packet.Length - length), linked.Token);
                                if (part.MessageType == WebSocketMessageType.Close)
                                    throw new IOException("Cloud WSS viewer closed.");
                                if (part.MessageType != WebSocketMessageType.Binary)
                                    throw new InvalidDataException(
                                        "Cloud WSS returned a nonbinary MAVLink packet.");
                                length += part.Count;
                            } while (!part.EndOfMessage);

                            int id;
                            framesReceived++;
                            if (!AeroHelmReceiveOnlyBridge.ValidFrame(packet, length,
                                out id))
                            {
                                framesRejected++;
                                continue;
                            }
                            if (messageIds.ContainsKey(id)) messageIds[id]++;
                            else if (messageIds.Count < 256) messageIds[id] = 1;
                            if (id == 0)
                            {
                                if (AeroHelmReceiveOnlyBridge.HasValidTelemetryChecksum(
                                    packet, length, 0))
                                {
                                    heartbeatsReceived++;
                                    quality.Observe(0);
                                }
                                else framesRejected++;
                            }
                            else if (id == 30)
                            {
                                float roll, pitch;
                                if (AeroHelmReceiveOnlyBridge.TryReadAttitude(
                                    packet, length, out roll, out pitch))
                                {
                                    attitudesReceived++;
                                    quality.Observe(30);
                                    lastRoll = roll;
                                    lastPitch = pitch;
                                }
                                else framesRejected++;
                            }
                        }
                    }
                    connectionIssue = "Cloud viewer disconnected; reconnecting.";
                }
                catch (OperationCanceledException)
                {
                    connectionIssue = DateTime.UtcNow >=
                        Program.AeroHelmTokenExpiresUtc ?
                        "Cloud session expired. Sign in again." :
                        "Cloud viewer disconnected.";
                }
                catch (Exception ex)
                {
                    // Do not report a missing attitude endpoint: this viewer
                    // does not use the optional REST preview endpoint.
                    connectionIssue = "Cloud WSS unavailable (" +
                        ex.GetType().Name + "). Check sign-in, viewer route and LINK.";
                }
                finally
                {
                    streamConnected = false;
                    quality.Reset();
                    RefreshDisplay();
                }
                if (shutdown.IsCancellationRequested ||
                    DateTime.UtcNow >= Program.AeroHelmTokenExpiresUtc)
                    break;
                try { await Task.Delay(1800, shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
            poll.Stop();
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
