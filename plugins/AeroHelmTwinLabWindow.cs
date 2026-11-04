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
                content=File.ReadAllText(file.FullName);
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
}
