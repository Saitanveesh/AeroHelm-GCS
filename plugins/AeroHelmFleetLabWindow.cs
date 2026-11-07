// AeroHelm Fleet Lab: advisory mission/task allocation only.
// No MAVLink command, lease, arm, launch, upload or cloud dispatch.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmFleetLabWindow : Form
    {
        private readonly TextBox report = new TextBox();
        private readonly Button propose = new Button();
        internal AeroHelmFleetLabWindow()
        {
            Text="AeroHelm FLEET — Advisory Scheduler Lab";
            Width=850;Height=660;MinimumSize=new Drawing.Size(630,450);
            StartPosition=FormStartPosition.CenterParent;
            BackColor=Drawing.Color.FromArgb(8,8,8);
            ForeColor=Drawing.Color.White;
            Font=new Drawing.Font("Segoe UI",11f);
            var layout=new TableLayoutPanel {
                Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,
                Padding=new Padding(18)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,86));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,62));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            Controls.Add(layout);
            layout.Controls.Add(new Label {
                Text="FLEET / ENERGY-AWARE DRAFT TASK ASSIGNMENT" +
                    Environment.NewLine +
                    "Operator-supplied planning data. No route is sent to an aircraft.",
                Dock=DockStyle.Fill,ForeColor=ForeColor,
                Font=new Drawing.Font("Segoe UI Semibold",12f)
            },0,0);
            propose.Text="SELECT FLEET JSON & SIMULATE ASSIGNMENT";
            propose.Dock=DockStyle.Left;propose.Width=350;propose.Height=43;
            propose.BackColor=Drawing.Color.FromArgb(35,35,35);
            propose.ForeColor=ForeColor;propose.FlatStyle=FlatStyle.Flat;
            propose.Click+=async(s,e)=>await RunAsync();
            layout.Controls.Add(propose,0,1);
            report.Dock=DockStyle.Fill;report.Multiline=true;
            report.ReadOnly=true;report.ScrollBars=ScrollBars.Both;
            report.WordWrap=false;report.BackColor=Drawing.Color.FromArgb(21,21,21);
            report.ForeColor=ForeColor;report.Font=new Drawing.Font("Consolas",10f);
            report.Text="NO AIRCRAFT DISPATCH. No geofence/terrain/weather clearance." +
                Environment.NewLine +
                "JSON: { vehicles: [...], tasks: [...] }" +
                Environment.NewLine +
                "Task selection is a draft for human review.";
            layout.Controls.Add(report,0,2);
            KeyPreview=true;
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){Close();e.Handled=true;}};
        }

        private async Task RunAsync()
        {
            if(!propose.Enabled)return;
            string raw;
            using(var picker=new OpenFileDialog {
                Filter="Fleet research JSON (*.json)|*.json",
                Title="Choose an offline fleet/task input",
                CheckFileExists=true,Multiselect=false
            })
            {
                if(picker.ShowDialog(this)!=DialogResult.OK)return;
                try
                {
                    var info=new FileInfo(picker.FileName);
                    if(info.Length<20 || info.Length>128*1024) {
                        report.Text="Fleet JSON must be 20 bytes to 128 KiB.";
                        return;
                    }
                    raw=File.ReadAllText(info.FullName);
                }
                catch(IOException ex) {
                    report.Text="Cannot read fleet draft: "+ex.GetType().Name;
                    return;
                }
                catch(UnauthorizedAccessException) {
                    report.Text="Access denied reading fleet draft.";
                    return;
                }
            }
            object payload;
            try { payload=new JavaScriptSerializer().DeserializeObject(raw); }
            catch(ArgumentException) {
                report.Text="Invalid fleet planning JSON."; return;
            }
            if(!(payload is System.Collections.Generic.Dictionary<string,object>)) {
                report.Text="Expected a JSON object containing vehicles and tasks.";
                return;
            }
            if(MessageBox.Show(this,
                "Submit operator-provided fleet/task data to the private cloud for " +
                "an OFFLINE allocation proposal? No command or upload will occur.",
                "AeroHelm FLEET — approve advisory evaluation",
                MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            propose.Enabled=false;
            report.Text="Requesting non-actuating proposal...";
            try
            {
                var result=await Task.Run(()=>OperationsApi.Request(
                    "POST","api/v3/fleet/schedule/draft",payload));
                if(IsDisposed||Disposing)return;
                report.Text="RESEARCH PROPOSAL / FLIGHT NOT AUTHORIZED"+
                    Environment.NewLine+Environment.NewLine+result;
            }
            catch(Exception ex)
            {
                if(!IsDisposed&&!Disposing)
                    report.Text="Advisory scheduler unavailable: "+ex.Message +
                        Environment.NewLine +
                        "No drone has been dispatched or given flight authority.";
            }
            finally { if(!IsDisposed&&!Disposing) propose.Enabled=true; }
        }
    }
}
