// Independent pilot-controlled direct UART/telemetry radio connection.
// Uses native Mission Planner serial transport. NEVER cloud/Oracle failover.
// Only the operator explicitly clicks CONNECT after reviewing the port.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using MissionPlanner;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmPilotDirectWindow : Form
    {
        private readonly MainV2 main;
        private readonly ComboBox ports = new ComboBox();
        private readonly ComboBox baudrates = new ComboBox();
        private readonly Label status = new Label();

        internal AeroHelmPilotDirectWindow(MainV2 mainForm)
        {
            if (mainForm == null) throw new ArgumentNullException("mainForm");
            main = mainForm;
            Text = VihangaVedikaProduct.Name + " — Direct USB / Telemetry Radio";
            Width = 800; Height = 480;
            MinimumSize = new Drawing.Size(620,420);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Drawing.Color.FromArgb(13,13,13);
            ForeColor = Drawing.Color.White;
            Font = new Drawing.Font("Segoe UI",11f);
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, Padding = new Padding(20),
                ColumnCount = 2, RowCount = 6
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,195));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,53));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,53));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,68));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,60));
            Controls.Add(layout);
            var heading = new Label {
                Dock = DockStyle.Fill,
                Text = "PILOT DIRECT / INDEPENDENT LOCAL COM CONNECTION" +
                    Environment.NewLine +
                    "Only an authorized on-site pilot may initiate this connection. " +
                    "The radio/USB link may carry real aircraft commands.",
                ForeColor = Drawing.Color.FromArgb(232,216,183)
            };
            layout.Controls.Add(heading,0,0);
            layout.SetColumnSpan(heading,2);
            layout.Controls.Add(new Label {
                Text="Physical COM port",Dock=DockStyle.Fill,
                TextAlign=Drawing.ContentAlignment.MiddleLeft
            },0,1);
            ports.Dock=DockStyle.Fill;ports.DropDownStyle=ComboBoxStyle.DropDownList;
            layout.Controls.Add(ports,1,1);
            layout.Controls.Add(new Label {
                Text="Radio baud rate",Dock=DockStyle.Fill,
                TextAlign=Drawing.ContentAlignment.MiddleLeft
            },0,2);
            baudrates.Dock=DockStyle.Fill;baudrates.DropDownStyle=ComboBoxStyle.DropDownList;
            baudrates.Items.AddRange(new object[] {
                "57600","115200","38400","230400","460800","921600"
            });
            baudrates.SelectedItem="57600";
            layout.Controls.Add(baudrates,1,2);
            var refresh = new Button {
                Text="REFRESH AVAILABLE COM PORTS",Dock=DockStyle.Fill,
                BackColor=Drawing.Color.FromArgb(42,42,42),ForeColor=ForeColor,
                FlatStyle=FlatStyle.Flat
            };
            refresh.Click+=(s,e)=>RefreshPorts();
            layout.Controls.Add(refresh,0,3);
            var native = new Button {
                Text="USE NATIVE MISSION PLANNER CONTROLS",Dock=DockStyle.Fill,
                BackColor=Drawing.Color.FromArgb(42,42,42),ForeColor=ForeColor,
                FlatStyle=FlatStyle.Flat
            };
            native.Click+=(s,e)=> {
                Close();
                if(!main.IsDisposed){main.MenuFlightData.PerformClick();main.Activate();}
            };
            layout.Controls.Add(native,1,3);
            status.Dock=DockStyle.Fill;status.AutoEllipsis=false;
            status.ForeColor=Drawing.Color.FromArgb(210,210,210);
            status.Text="NO AUTOMATIC FAILOVER. Neither selecting a COM port " +
                "nor opening this window connects to the FC. " +
                "Ensure local radio/USB equipment and appropriate onboard " +
                "flight-controller failsafes are configured independently.";
            layout.Controls.Add(status,0,4);layout.SetColumnSpan(status,2);
            var connect = new Button {
                Text="CONNECT SELECTED DIRECT LINK",Dock=DockStyle.Fill,
                BackColor=Drawing.Color.FromArgb(78,75,49),
                ForeColor=ForeColor,FlatStyle=FlatStyle.Flat
            };
            connect.Click+=(s,e)=>ConnectDirect();
            layout.Controls.Add(connect,0,5);layout.SetColumnSpan(connect,2);
            Shown+=(s,e)=>RefreshPorts();
            KeyPreview=true;
            KeyDown+=(s,e)=> {
                if(e.KeyCode==Keys.Escape){Close();e.Handled=true;}
            };
        }

        private void RefreshPorts()
        {
            string previous=ports.SelectedItem as string;
            ports.Items.Clear();
            try
            {
                foreach(string name in SerialPort.GetPortNames()
                    .Where(x=>Regex.IsMatch(x, @"^COM[0-9]{1,4}$",
                        RegexOptions.IgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))
                    ports.Items.Add(name);
            }
            catch(Exception ex) when (ex is UnauthorizedAccessException ||
                                      ex is System.IO.IOException ||
                                      ex is InvalidOperationException)
            {
                status.Text="Cannot list Windows serial ports: "+ex.GetType().Name;
                return;
            }
            if(previous!=null && ports.Items.Contains(previous))
                ports.SelectedItem=previous;
            else if(ports.Items.Count>0)
                ports.SelectedIndex=0;
            status.Text=ports.Items.Count==0 ?
                "NO COM PORTS FOUND. Attach independent local telemetry-radio/USB hardware; " +
                "cloud connectivity cannot substitute for a physical pilot radio." :
                "Found "+ports.Items.Count+" local serial port(s). " +
                "Verify the actual radio COM port and baud before connecting. " +
                "No automatic failover or FC parameter change has occurred.";
        }

        private void ConnectDirect()
        {
            string port=ports.SelectedItem as string;
            string baud=baudrates.SelectedItem as string;
            if(port==null || !Regex.IsMatch(port,@"^COM[0-9]{1,4}$") ||
               baud==null || !Regex.IsMatch(baud,@"^[0-9]{4,6}$"))
            {
                MessageBox.Show(this,"Select a valid observed COM port and baud rate.",
                    "Vihanga Vedika — Direct Radio",MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            if(!SerialPort.GetPortNames().Any(x=>
                string.Equals(x,port,StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this,"The selected COM port is no longer available.",
                    "Vihanga Vedika — Radio Unavailable",MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                RefreshPorts();return;
            }
            if(main.IsDisposed || MainV2.comPort.BaseStream.IsOpen)
            {
                MessageBox.Show(this,
                    "An aircraft link may already be active. Disconnect it "+
                    "explicitly in native Mission Planner before changing links.",
                    "Vihanga Vedika — Connection Change Blocked",MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            if(MessageBox.Show(this,
                "Connect Mission Planner DIRECTLY to "+port+" at "+baud+" baud?" +
                Environment.NewLine+Environment.NewLine+
                "The direct radio/USB path can transmit real MAVLink "+
                "parameter requests and aircraft commands. It is independent "+
                "from cloud telemetry. This does NOT switch flight modes "+
                "or transfer authority automatically."+
                Environment.NewLine+
                "Proceed only as the on-site authorized pilot.",
                "Vihanga Vedika — Confirm Radio Connection",
                MessageBoxButtons.YesNo,MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2)!=DialogResult.Yes)
                return;
            Close();
            // Ensure the UI closes before the native connection procedure.
            // No cloud command path, background reconnect or FC actuation.
            if(!main.IsDisposed && main.IsHandleCreated)
                main.BeginInvoke((Action)(()=>{
                    try
                    {
                        main.MenuFlightData.PerformClick();
                        main.doConnect(MainV2.comPort,port,baud,true,true);
                    }
                    catch(Exception ex)
                    {
                        MessageBox.Show(main,
                            "Direct pilot link failed: "+ex.GetType().Name+
                            ". Inspect USB/radio and FC settings locally.",
                            "Vihanga Vedika — Radio Connection Unavailable",
                            MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    }
                }));
        }
    }
}
