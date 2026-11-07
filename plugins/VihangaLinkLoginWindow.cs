// Sign in to a separately deployed private Vihanga MAVLink relay.
// Operator credentials are sent only to an exact, approved HTTPS origin and
// neither password nor short-lived viewer JWT is persisted to disk.
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
    internal sealed class VihangaLinkLoginWindow : Form
    {
        private readonly TextBox username = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly Button login = new Button();
        private readonly Label status = new Label();

        private VihangaLinkLoginWindow()
        {
            Text = "Vihanga private telemetry — sign in";
            Width = 660;
            Height = 280;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, Padding = new Padding(16),
                ColumnCount = 2, RowCount = 5
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            var target = new TextBox {
                Text = VihangaLinkSession.TargetOrigin,
                ReadOnly = true, Dock = DockStyle.Fill, TabStop = false
            };
            username.Text = "operator";
            username.Dock = DockStyle.Fill;
            password.Dock = DockStyle.Fill;
            password.UseSystemPasswordChar = true;
            layout.Controls.Add(new Label { Text = "Private relay",
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 0);
            layout.Controls.Add(target, 1, 0);
            layout.Controls.Add(new Label { Text = "Operator",
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 1);
            layout.Controls.Add(username, 1, 1);
            layout.Controls.Add(new Label { Text = "Password",
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 2);
            layout.Controls.Add(password, 1, 2);
            login.Text = "Connect telemetry";
            login.AutoSize = true;
            login.Click += async (s, e) => await SignInAsync();
            var cancel = new Button {
                Text = "Cancel", AutoSize = true,
                DialogResult = DialogResult.Cancel
            };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            actions.Controls.Add(login);
            actions.Controls.Add(cancel);
            layout.Controls.Add(actions, 1, 3);
            status.Text = "Independent read-only telemetry; no remote aircraft commands.";
            status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            layout.Controls.Add(status, 0, 4);
            layout.SetColumnSpan(status, 2);
            AcceptButton = login;
            CancelButton = cancel;
            Shown += (s, e) => password.Focus();
        }

        internal static bool EnsureSignedIn(Form owner)
        {
            if (!VihangaLinkSession.UsesDedicatedRelay ||
                VihangaLinkSession.Authenticated)
                return true;
            try
            {
                using (var window = new VihangaLinkLoginWindow())
                    return window.ShowDialog(owner) == DialogResult.OK &&
                           VihangaLinkSession.Authenticated;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Private telemetry relay unavailable: " +
                    ex.Message, "Vihanga telemetry connection",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private async Task SignInAsync()
        {
            if (string.IsNullOrWhiteSpace(username.Text) ||
                string.IsNullOrEmpty(password.Text))
            {
                status.Text = "Enter the relay operator credentials.";
                return;
            }
            login.Enabled = false;
            string secret = password.Text;
            password.Clear();
            try
            {
                string origin = VihangaLinkSession.TargetOrigin;
                string user = username.Text.Trim();
                var response = await Task.Run(() => Authenticate(origin, user, secret));
                if (IsDisposed || Disposing) return;
                object token, expires;
                if (!response.TryGetValue("access_token", out token) ||
                    !response.TryGetValue("expires_in", out expires))
                    throw new InvalidDataException("Invalid relay authentication response.");
                VihangaLinkSession.Accept(origin, Convert.ToString(token),
                    Convert.ToInt32(expires));
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (WebException ex)
            {
                if (!IsDisposed && !Disposing)
                {
                    var response = ex.Response as HttpWebResponse;
                    status.Text = response == null ? "Private relay unreachable." :
                        "Relay authentication rejected (HTTP " +
                        (int)response.StatusCode + ").";
                    if (response != null) response.Dispose();
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    status.Text = "Relay sign-in unavailable: " + ex.GetType().Name;
            }
            finally
            {
                secret = null;
                if (!IsDisposed && !Disposing) login.Enabled = true;
            }
        }

        private static Dictionary<string, object> Authenticate(
            string origin, string user, string secret)
        {
            Uri endpoint = new Uri(new Uri(origin + "/"), "api/v1/auth/login");
            byte[] payload = Encoding.UTF8.GetBytes(
                new JavaScriptSerializer().Serialize(
                    new { username = user, password = secret }));
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.ContentLength = payload.Length;
            request.AllowAutoRedirect = false;
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            using (var stream = request.GetRequestStream())
                stream.Write(payload, 0, payload.Length);
            Array.Clear(payload, 0, payload.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(),
                                                Encoding.UTF8))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException("Unexpected login response.");
                return new JavaScriptSerializer().Deserialize<
                    Dictionary<string, object>>(reader.ReadToEnd());
            }
        }
    }
}
