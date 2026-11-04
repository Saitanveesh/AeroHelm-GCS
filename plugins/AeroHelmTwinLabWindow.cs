// AeroHelm Twin Lab: operator-controlled offline calibration experiment.
// No autopilot upload, parameter changes, or flight authority.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using MissionPlanner;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmTwinLabWindow : Form
    {
        private readonly TextBox results = new TextBox();
        private readonly Button analyze = new Button();
        private readonly Label location = new Label();

        internal AeroHelmTwinLabWindow()
        {
            Text = "AeroHelm — DIGITAL TWIN RESEARCH LAB";
            Width = 880; Height = 690; MinimumSize = new Drawing.Size(640,480);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Drawing.Color.FromArgb(9,9,9);
            ForeColor = Drawing.Color.FromArgb(236,236,236);
            Font = new Drawing.Font("Segoe UI",11f);
            var panel = new TableLayoutPanel {
                Dock=DockStyle.Fill,Padding=new Padding(20),
                ColumnCount=1,RowCount=4
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute,72));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute,68));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            Controls.Add(panel);
            panel.Controls.Add(new Label {
                Text="DIGITAL TWIN / INDEPENDENT REFERENCE CALIBRATION" +
                    Environment.NewLine +
                    "Research-only 2D velocity/heading fit. Never installed on an aircraft.",
                Dock=DockStyle.Fill,ForeColor=ForeColor,
                Font=new Drawing.Font("Segoe UI Semibold",12f)},0,0);
            location.Text="No reference file selected. JSON must contain training, validation and reference_source.";
            location.Dock=DockStyle.Fill;
            location.AutoEllipsis=true;
            location.ForeColor=Drawing.Color.FromArgb(190,190,190);
            panel.Controls.Add(location,0,1);
            analyze.Text="SELECT REFERENCE JSON & EVALUATE";
            analyze.Dock=DockStyle.Left;
            analyze.Width=310;
            analyze.Height=44;
            analyze.BackColor=Drawing.Color.FromArgb(40,40,40);
            analyze.ForeColor=ForeColor;
            analyze.FlatStyle=FlatStyle.Flat;
            analyze.Click+=async(s,e)=>await EvaluateAsync();
            panel.Controls.Add(analyze,0,2);
            results.Multiline=true;
            results.ReadOnly=true;
            results.ScrollBars=ScrollBars.Both;
            results.WordWrap=false;
            results.Dock=DockStyle.Fill;
            results.BackColor=Drawing.Color.FromArgb(22,22,22);
            results.ForeColor=ForeColor;
            results.Font=new Drawing.Font("Consolas",10f);
            results.Text="No model is installed or given flight-control authority."+
                Environment.NewLine+
                "Use separate, independently timestamped reference motion data."+
                Environment.NewLine+
                "Training and held-out validation measurements must be disjoint.";
            panel.Controls.Add(results,0,3);
            KeyPreview=true;
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){Close();e.Handled=true;}};
        }

        private async Task EvaluateAsync()
        {
            if (!analyze.Enabled) return;
            string content;
            using(var choose=new OpenFileDialog {
                Filter="Reference motion JSON (*.json)|*.json",
                CheckFileExists=true,Multiselect=false,
                Title="Select offline training/validation reference samples"
            })
            {
                if(choose.ShowDialog(this)!=DialogResult.OK)return;
                var file=new FileInfo(choose.FileName);
                if(file.Length<20 || file.Length>128*1024)
                {
                    results.Text="Reference file must be JSON, 20 bytes to 128 KiB.";
                    return;
                }
                try {
                    content=File.ReadAllText(file.FullName);
                }
                catch(IOException ex) {
                    results.Text="Cannot read reference file: "+ex.GetType().Name;
                    return;
                }
                catch(UnauthorizedAccessException) {
                    results.Text="Permission denied reading reference file.";
                    return;
                }
                location.Text="Selected: "+file.Name+" (reference accuracy NOT verified)";
            }
            object payload;
            try { payload=new JavaScriptSerializer().DeserializeObject(content); }
            catch(ArgumentException ex) { results.Text="Invalid JSON: "+ex.Message; return; }
            if(!(payload is System.Collections.Generic.Dictionary<string,object>))
            {
                results.Text="Expected a JSON object with training, validation and reference_source.";
                return;
            }
            if(MessageBox.Show(this,
                "Send this reference data to the private AeroHelm cloud for an " +
                "OFFLINE model-fit experiment? No calibration is applied to Pixhawk.",
                "AeroHelm Twin Lab — confirm data processing",
                MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            analyze.Enabled=false;
            results.Text="Submitting read-only model-evaluation request...";
            try
            {
                var response=await Task.Run(()=>OperationsApi.Request(
                    "POST","api/v3/research/twin-lab/evaluate",payload));
                if(IsDisposed || Disposing)return;
                results.Text="EXPERIMENTAL RESULT — NOT A FLIGHT-CERTIFIED TWIN" +
                    Environment.NewLine + Environment.NewLine + response;
            }
            catch(Exception ex)
            {
                if(!IsDisposed&&!Disposing)
                    results.Text="Model evaluation unavailable: "+ex.Message+
                        Environment.NewLine+"No model applied. Verify V3 research backend is enabled.";
            }
            finally
            {
                if(!IsDisposed&&!Disposing)analyze.Enabled=true;
            }
        }
    }
    // Offline operator-supplied six-DOF rigid-body model, not a certified
    // digital twin. Independent from actual Mission Planner/Pixhawk control.
    internal sealed class AeroHelmPhysicsLabWindow : Form
    {
        private readonly TextBox result = new TextBox();
        private readonly Button evaluate = new Button();
        private readonly Label selected = new Label();

        internal AeroHelmPhysicsLabWindow()
        {
            Text="AeroHelm — SIX-DOF PHYSICS RESEARCH (UNVALIDATED)";
            Width=900;Height=690;MinimumSize=new Drawing.Size(640,480);
            StartPosition=FormStartPosition.CenterParent;
            BackColor=Drawing.Color.FromArgb(9,9,9);
            ForeColor=Drawing.Color.FromArgb(237,237,237);
            Font=new Drawing.Font("Segoe UI",11f);
            KeyPreview=true;
            var layout=new TableLayoutPanel {
                Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=4
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,80));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            Controls.Add(layout);
            layout.Controls.Add(new Label {
                Text="PHYSICS LAB / BOUNDED 6-DOF RIGID-BODY INTEGRATION"+
                    Environment.NewLine+
                    "Offline model only — NO calibrated flight dynamics, motor commands or authorization.",
                Dock=DockStyle.Fill,ForeColor=ForeColor,
                Font=new Drawing.Font("Segoe UI Semibold",12f)
            },0,0);
            selected.Dock=DockStyle.Fill;
            selected.Text="Select a local physics JSON (mass, inertia, initial state, thrust, torque).";
            selected.ForeColor=Drawing.Color.FromArgb(190,190,190);
            selected.AutoEllipsis=true;
            layout.Controls.Add(selected,0,1);
            evaluate.Text="SELECT PHYSICS JSON & SIMULATE";
            evaluate.Dock=DockStyle.Left;evaluate.Width=330;
            evaluate.FlatStyle=FlatStyle.Flat;
            evaluate.BackColor=Drawing.Color.FromArgb(40,40,40);
            evaluate.ForeColor=ForeColor;
            evaluate.Click+=async(s,e)=>await EvaluateAsync();
            layout.Controls.Add(evaluate,0,2);
            result.Dock=DockStyle.Fill;result.Multiline=true;result.ReadOnly=true;
            result.WordWrap=false;result.ScrollBars=ScrollBars.Both;
            result.Font=new Drawing.Font("Consolas",10f);
            result.BackColor=Drawing.Color.FromArgb(22,22,22);
            result.ForeColor=ForeColor;
            result.Text="Research V3 backend must be enabled on Oracle."+
                Environment.NewLine+
                "Units: ENU meters, SI mass/inertia, quaternion wxyz."+
                Environment.NewLine+
                "Missing: motor curves, battery, wind turbulence, sensor latency."+
                Environment.NewLine+
                "Results are advisory and never sent to the aircraft.";
            layout.Controls.Add(result,0,3);
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){Close();e.Handled=true;}};
        }

        private async Task EvaluateAsync()
        {
            if(!evaluate.Enabled)return;
            string source;
            using(var dialog=new OpenFileDialog {
                Filter="Physics model JSON (*.json)|*.json",
                CheckFileExists=true,Multiselect=false,
                Title="Choose local unvalidated physics model input"
            })
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                var file=new FileInfo(dialog.FileName);
                if(file.Length<24 || file.Length>128*1024) {
                    result.Text="Physics input must be 24 bytes to 128 KiB.";
                    return;
                }
                try { source=File.ReadAllText(file.FullName); }
                catch(IOException) { result.Text="Cannot read physics input file.";return; }
                catch(UnauthorizedAccessException) { result.Text="File permission denied.";return; }
                selected.Text="Operator-supplied: "+file.Name+
                    " (no real airframe parameters have been verified)";
            }
            object payload;
            try { payload=new JavaScriptSerializer().DeserializeObject(source); }
            catch(ArgumentException) { result.Text="Invalid JSON.";return; }
            var root=payload as System.Collections.Generic.Dictionary<string,object>;
            if(root==null || !root.ContainsKey("model") || !root.ContainsKey("control"))
            {
                result.Text="Required JSON objects: model, control. See Tools/fixtures/physics_lab_hover.json.";
                return;
            }
            if(MessageBox.Show(this,
                "Send this operator-supplied physics model to the PRIVATE cloud "+
                "for bounded offline simulation? It will not alter the "+
                "Pixhawk, Mission Planner or live telemetry.",
                "AeroHelm physics research — confirm",
                MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            evaluate.Enabled=false;
            result.Text="Running authenticated offline physics research...";
            try
            {
                var data=await Task.Run(()=>OperationsApi.Request(
                    "POST","api/v3/research/physics-lab/evaluate",payload));
                if(IsDisposed||Disposing)return;
                result.Text="UNVALIDATED SIX-DOF PHYSICS RESEARCH — NOT A FLIGHT TWIN"+
                    Environment.NewLine+Environment.NewLine+data;
            }
            catch(Exception ex)
            {
                if(!IsDisposed&&!Disposing)
                    result.Text="Physics lab unavailable: "+ex.Message+
                        Environment.NewLine+
                        "Research V3 is optional and may not be deployed. No aircraft command occurred.";
            }
            finally { if(!IsDisposed&&!Disposing)evaluate.Enabled=true; }
        }
    }

}
