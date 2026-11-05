// AeroHelm PATHS — explicit cloud-observation versus direct-pilot link choice.
// The controller's normal RC/failsafe policy is external to this UI.
// This form NEVER opens a serial port, sends MAVLink, changes FC parameters,
// or transfers flight authority automatically.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal enum AeroHelmLinkPath { Cancel, CloudReadOnly, PilotDirect }

    internal sealed class AeroHelmLinkSelector : Form
    {
        internal AeroHelmLinkPath Selection { get; private set; }

        internal AeroHelmLinkSelector()
        {
            Selection = AeroHelmLinkPath.Cancel;
            Text = "AeroHelm PATHS — select flight-data link";
            Width = 810; Height = 520;
            MinimumSize = new Drawing.Size(620, 460);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Drawing.Color.FromArgb(9,9,9);
            ForeColor = Drawing.Color.White;
            Font = new Drawing.Font("Segoe UI", 11f);
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
                Padding = new Padding(22), BackColor = BackColor
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 85));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 114));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 114));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
            cloud.Click += (sender,args)=>Select(AeroHelmLinkPath.CloudReadOnly);
            layout.Controls.Add(cloud,0,1);

            var direct = Action(
                "PILOT BACKUP — native USB / telemetry-radio COM",
                "Use Mission Planner's normal COM port and baud controls; " +
                "hardware radio must be independently wired and bench-tested. " +
                "This selection does NOT connect or arm automatically.");
            direct.Click += (sender,args)=>Select(AeroHelmLinkPath.PilotDirect);
            layout.Controls.Add(direct,0,2);
            layout.Controls.Add(new Label {
                Dock=DockStyle.Fill,
                ForeColor=Drawing.Color.FromArgb(205,187,153),
                Text="NO AUTO-FAILOVER: cloud loss only raises an alert. " +
                     "The pilot deliberately selects the independent local radio/USB path. " +
                     "Never treat network reconnection as permission to transmit commands."
            },0,3);
            var cancel=new Button {
                Text="CANCEL",Dock=DockStyle.Right,Width=160,Height=44,
                DialogResult=DialogResult.Cancel,
                FlatStyle=FlatStyle.Flat,
                ForeColor=ForeColor,BackColor=Drawing.Color.FromArgb(43,43,43)
            };
            layout.Controls.Add(cancel,0,4);
            CancelButton=cancel;
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
                UseVisualStyleBackColor=false
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
}
