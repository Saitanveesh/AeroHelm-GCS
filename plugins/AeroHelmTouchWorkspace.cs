// AeroHelm Touch Workspace — reference 3x3 touch-first launch surface.
// The original Mission Planner form and comm stack are never replaced.
// GPL-3.0-or-later. No MAVLink commands originate here.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using MissionPlanner;

namespace AeroHelm.GcsCloud
{
    internal sealed class AeroHelmTouchWindow : Form
    {
        private readonly TableLayoutPanel tiles = new TableLayoutPanel();
        private readonly Label heading = new Label();
        private readonly Label footer = new Label();
        private readonly List<Button> buttons = new List<Button>();
        private bool opening;

        private static readonly Drawing.Color Back = Drawing.Color.FromArgb(8, 8, 8);
        private static readonly Drawing.Color Tile = Drawing.Color.FromArgb(37, 37, 37);
        private static readonly Drawing.Color Fore = Drawing.Color.FromArgb(246, 246, 246);

        private sealed class Destination
        {
            internal readonly string Name;
            internal readonly string Glyph;
            internal readonly Action Open;
            internal readonly string Description;
            internal Destination(string name,string glyph,string description,Action open)
            { Name=name;Glyph=glyph;Description=description;Open=open; }
        }

        public AeroHelmTouchWindow()
        {
            Text = "AeroHelm — Touch Workspace";
            StartPosition = FormStartPosition.CenterParent;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Drawing.Size(610, 540);
            BackColor = Back;
            ForeColor = Fore;
            Font = new Drawing.Font("Segoe UI", 12);
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;

            var top = new TableLayoutPanel {Dock=DockStyle.Top, Height=82, ColumnCount=3,
                BackColor=Back, Padding=new Padding(18,10,18,6)};
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            var back = new Button {Text="← MISSION PLANNER", Dock=DockStyle.Fill,
                FlatStyle=FlatStyle.Flat, BackColor=Tile, ForeColor=Fore, TabStop=true};
            back.FlatAppearance.BorderSize=0;
            back.Click+=(s,e)=>Close();
            heading.Text="A E R O H E L M";
            heading.Font=new Drawing.Font("Segoe UI Semibold",17,Drawing.FontStyle.Bold);
            heading.Dock=DockStyle.Fill;
            heading.TextAlign=Drawing.ContentAlignment.MiddleCenter;
            var close=new Button {Text="CLOSE PANEL  ×",Dock=DockStyle.Fill,
                BackColor=Tile,ForeColor=Fore,FlatStyle=FlatStyle.Flat};
            close.FlatAppearance.BorderSize=0;
            close.Click+=(s,e)=>Close();
            top.Controls.Add(back,0,0);
            top.Controls.Add(heading,1,0);
            top.Controls.Add(close,2,0);

            footer.Text="AeroHelm Operator Workspace  •  Mission Planner remains the flight-control foundation  •  No new flight permissions";
            footer.Dock=DockStyle.Bottom;
            footer.Height=45;
            footer.TextAlign=Drawing.ContentAlignment.MiddleCenter;
            footer.ForeColor=Drawing.Color.FromArgb(174,174,174);
            footer.Font=new Drawing.Font("Segoe UI",10);

            var outer=new Panel {Dock=DockStyle.Fill,Padding=new Padding(26,12,26,12)};
            tiles.Dock=DockStyle.Fill;
            tiles.ColumnCount=3;
            tiles.RowCount=3;
            tiles.Padding=new Padding(0);
            for(int i=0;i<3;i++)
            {
                tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/3));
                tiles.RowStyles.Add(new RowStyle(SizeType.Percent,100f/3));
            }
            var sections = new [] {
                new Destination("FLIGHT","✈","Mission Planner's native Flight Data panel",OpenFlight),
                new Destination("PLAN","▤","Mission Planner's native Flight Plan panel",OpenPlan),
                new Destination("VISION","▣","Private camera page (server required)",()=>ShowWindow(new AeroHelmVisionWindow())),
                new Destination("OPS","☷","Simulated mission & supervised bench maintenance",()=>ShowWindow(new AeroHelmOpsWindow())),
                new Destination("SECURITY","⬡","Cloud security and audit diagnostics",()=>ShowWindow(new AeroHelmIntelWindow())),
                new Destination("FLEET","▱","Authenticated fleet health and operator view",()=>ShowWindow(new AeroHelmIntelWindow())),
                new Destination("LINK","⌁","Cloud connection and freshness",()=>ShowWindow(new AeroHelmLinkWindow())),
                new Destination("ANALYTICS","▥","Historical telemetry and flight reports",()=>ShowWindow(new AeroHelmIntelWindow())),
                new Destination("SETTINGS","⚙","Native Mission Planner settings",OpenSettings)
            };
            for(int i=0;i<sections.Length;i++)
            {
                var section=sections[i];
                var button=new TouchTile(section.Name,section.Glyph,section.Description);
                button.Dock=DockStyle.Fill;
                button.Margin=new Padding(10);
                button.Click+=(s,e)=>Activate(section);
                tiles.Controls.Add(button,i%3,i/3);
                buttons.Add(button);
            }
            outer.Controls.Add(tiles);
            Controls.Add(outer);
            Controls.Add(top);
            Controls.Add(footer);
            Resize+=(s,e)=>LayoutForScreen();
            KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){Close();e.Handled=true;}};
            LayoutForScreen();
        }

        private void LayoutForScreen()
        {
            bool narrow=ClientSize.Width<900;
            heading.Font=new Drawing.Font("Segoe UI Semibold",narrow?12:17,Drawing.FontStyle.Bold);
            foreach(var button in buttons)
                ((TouchTile)button).Compact=ClientSize.Height<670 || narrow;
        }

        private void Activate(Destination section)
        {
            if(opening)return;
            opening=true;
            try { section.Open(); }
            catch(Exception ex)
            {
                MessageBox.Show(this,"Unable to open "+section.Name+": "+
                    ex.Message,"AeroHelm",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
            finally{opening=false;}
        }

        private void ShowWindow(Form form)
        {
            using(form) form.ShowDialog(this);
        }

        // Native MainV2 owns the COM/UART/control logic; AeroHelm does not
        // synthesize click events that arm, change mode or upload missions.
        private void OpenFlight()
        {
            Close();
            var main=MainV2.instance;
            main.Activate();
            // Native Flight Data tab is already the startup/default view.
            // A direct tab activation is wired in the plugin on verified APIs.
        }
        private void OpenPlan()
        {
            Close();
            var main=MainV2.instance;
            main.Activate();
            MessageBox.Show(main,
                "Open the native FLIGHT PLAN tab to edit and review routes.\n"+
                "AeroHelm SURVEY generates offline waypoint drafts; it never uploads flights.",
                "AeroHelm PLAN",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        private void OpenSettings()
        {
            Close();
            var main=MainV2.instance;
            main.Activate();
            MessageBox.Show(main,
                "Open Mission Planner's SETUP or CONFIG/TUNING tab for supported local configuration.\n"+
                "Remote writes and calibration are not authorized by the touch panel.",
                "AeroHelm SETTINGS",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }

        private sealed class TouchTile : Button
        {
            internal bool Compact {get;set;}
            private readonly string caption;
            private readonly string glyph;
            private readonly string hint;
            public TouchTile(string caption,string glyph,string hint)
            {
                this.caption=caption;
                this.glyph=glyph;
                this.hint=hint;
                AccessibleName=caption;
                AccessibleDescription=hint;
                AutoEllipsis=true;
                FlatStyle=FlatStyle.Flat;
                FlatAppearance.BorderSize=0;
                FlatAppearance.MouseOverBackColor=Drawing.Color.FromArgb(55,55,55);
                FlatAppearance.MouseDownBackColor=Drawing.Color.FromArgb(70,70,70);
                BackColor=Tile;
                ForeColor=Fore;
                UseVisualStyleBackColor=false;
                TabStop=true;
                TextAlign=Drawing.ContentAlignment.MiddleCenter;
                Resize+=(s,e)=>Reflow();
                Reflow();
            }

            private void Reflow()
            {
                int title=Compact?11:16;
                Font=new Drawing.Font("Segoe UI",title,Drawing.FontStyle.Regular);
                Text=glyph+Environment.NewLine+Environment.NewLine+caption;
            }
        }
    }
}
