// Authenticated, simulation-only cloud mission preview. No MAVLink command path.
extern alias FrameworkDrawing;
using Drawing = FrameworkDrawing::System.Drawing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AeroHelm.GcsCloud
{
    internal sealed class VihangaMissionPreviewWindow : Form
    {
        private readonly TextBox input = new TextBox();
        private readonly TextBox output = new TextBox();
        private readonly Button preview = new Button();
        private readonly Label status = new Label();

        internal VihangaMissionPreviewWindow()
        {
            Text = "Vihanga | Cloud mission preview — simulation only";
            Width = 920; Height = 720;
            MinimumSize = new Drawing.Size(650, 470);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Drawing.Font("Segoe UI", 10f);
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
                Padding = new Padding(12)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(layout);

            layout.Controls.Add(new Label {
                Text = "Simulation-only route review: load a mission JSON " +
                       "with vehicle_id, bounds, route, speed_mps, " +
                       "estimated_max_flight_seconds and reserve_percent. " +
                       "This does not upload or execute a mission.",
                Dock = DockStyle.Fill, AutoEllipsis = true
            }, 0, 0);
            input.Dock = DockStyle.Fill;
            input.Multiline = true;
            input.ScrollBars = ScrollBars.Both;
            input.WordWrap = false;
            input.Font = new Drawing.Font("Consolas", 10f);
            layout.Controls.Add(input, 0, 1);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            var import = new Button { Text = "Open JSON", AutoSize = true };
            preview.Text = "Preview route";
            preview.AutoSize = true;
            var cancel = new Button { Text = "Close", AutoSize = true };
            import.Click += (s,e) => ImportJson();
            preview.Click += async (s,e) => await PreviewAsync();
            cancel.Click += (s,e) => Close();
            actions.Controls.Add(import);
            actions.Controls.Add(preview);
            actions.Controls.Add(cancel);
            layout.Controls.Add(actions, 0, 2);

            output.Dock = DockStyle.Fill;
            output.Multiline = true;
            output.ReadOnly = true;
            output.WordWrap = false;
            output.ScrollBars = ScrollBars.Both;
            output.Font = new Drawing.Font("Consolas", 10f);
            layout.Controls.Add(output, 0, 3);
            status.Dock = DockStyle.Fill;
            status.Text = "No mission transmitted. Output is an uncalibrated planning estimate.";
            status.AutoEllipsis = true;
            layout.Controls.Add(status, 0, 4);
        }

        private void ImportJson()
        {
            using (var file = new OpenFileDialog {
                Filter = "JSON route preview (*.json)|*.json",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (file.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var length = new FileInfo(file.FileName).Length;
                    if (length < 2 || length > 65536)
                        throw new InvalidDataException("Mission JSON exceeds the 64 KB preview limit.");
                    input.Text = File.ReadAllText(file.FileName, Encoding.UTF8);
                }
                catch (Exception ex) when (ex is IOException ||
                                           ex is UnauthorizedAccessException ||
                                           ex is InvalidDataException)
                {
                    status.Text = "Cannot read mission preview JSON: " + ex.Message;
                }
            }
        }

        private async Task PreviewAsync()
        {
            if (IsDisposed || Disposing) return;
            if (!VihangaLinkSession.UsesDedicatedRelay ||
                !VihangaLinkSession.Authenticated)
            {
                status.Text = "Sign in to the dedicated private relay through PATHS first.";
                return;
            }
            string body = input.Text;
            if (string.IsNullOrWhiteSpace(body) ||
                Encoding.UTF8.GetByteCount(body) > 65536)
            {
                status.Text = "Supply a valid mission JSON document under 64 KB.";
                return;
            }
            preview.Enabled = false;
            status.Text = "Validating cloud simulation preview...";
            try
            {
                string origin = VihangaLinkSession.TargetOrigin;
                string token = VihangaLinkSession.AccessToken;
                string result = await Task.Run(() => RequestPreview(origin, token, body));
                if (IsDisposed || Disposing) return;
                var data = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(result);
                object ready, sha, distance, duration, captures;
                if (!data.TryGetValue("status", out ready) ||
                    Convert.ToString(ready) != "SIMULATION_PREVIEW_ONLY" ||
                    !data.TryGetValue("vehicle_command_authorized", out ready) ||
                    !(ready is bool) || (bool)ready)
                    throw new InvalidDataException("Unexpected preview safety contract.");
                data.TryGetValue("revision_sha256", out sha);
                data.TryGetValue("horizontal_distance_m", out distance);
                data.TryGetValue("estimated_seconds", out duration);
                data.TryGetValue("camera_capture_estimate", out captures);
                output.Text = "SIMULATION ONLY" + Environment.NewLine +
                    "Revision: " + Convert.ToString(sha) + Environment.NewLine +
                    "Distance (m): " + Convert.ToString(distance) + Environment.NewLine +
                    "Estimated duration (s): " + Convert.ToString(duration) +
                    Environment.NewLine +
                    "Estimated captures: " + Convert.ToString(captures) +
                    Environment.NewLine + Environment.NewLine + result;
                status.Text = "Preview complete. No aircraft mission or command was sent.";
            }
            catch (WebException ex)
            {
                if (!IsDisposed && !Disposing)
                    status.Text = "Private simulation preview unavailable: " + ex.Status;
                if (ex.Response != null) ex.Response.Dispose();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    status.Text = "Preview validation failed: " + ex.GetType().Name;
            }
            finally
            {
                if (!IsDisposed && !Disposing) preview.Enabled = true;
            }
        }

        private static string RequestPreview(string origin, string token, string body)
        {
            var url = new Uri(new Uri(origin + "/"), "api/v1/sim/mission/preview");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.AllowAutoRedirect = false;
            req.Timeout = 12000;
            req.ReadWriteTimeout = 12000;
            req.Accept = "application/json";
            req.ContentType = "application/json";
            req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            byte[] raw = Encoding.UTF8.GetBytes(body);
            req.ContentLength = raw.Length;
            try
            {
                using (var stream = req.GetRequestStream())
                    stream.Write(raw, 0, raw.Length);
                using (var response = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(
                       response.GetResponseStream(), Encoding.UTF8))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidDataException("Unexpected preview HTTP response.");
                    return reader.ReadToEnd();
                }
            }
            finally { Array.Clear(raw, 0, raw.Length); }
        }
    }
}
