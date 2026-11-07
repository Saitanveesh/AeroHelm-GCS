// AeroHelm product startup authentication, not a Pixhawk security boundary.
// GPL-3.0-or-later. Do not persist operator passwords or bearer tokens.
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MissionPlanner
{
    internal static class AeroHelmStartupGate
    {
        public static bool ShouldRequire(string[] args)
        {
            // Explicit local-maintenance escape hatch keeps offline USB diagnostics
            // and recovery possible. It does NOT grant cloud credentials.
            foreach (string a in args ?? new string[0])
                if (string.Equals(a, "--local-maintenance",
                                  StringComparison.OrdinalIgnoreCase))
                    return false;
            // Dedicated Vihanga telemetry uses a separate short-lived JWT
            // obtained through PATHS, not the legacy Oracle M1 startup token.
            // Permit the native shell to load without an M1 credential only
            // when the launcher explicitly selected read-only private relay mode.
            // PATHS still denies cloud access until relay sign-in succeeds.
            string relay = Environment.GetEnvironmentVariable("VIHANGA_LINK_ORIGIN");
            if (!string.IsNullOrWhiteSpace(relay) &&
                string.Equals(Environment.GetEnvironmentVariable(
                    "AEROHELM_CLOUD_READONLY"), "1", StringComparison.Ordinal))
            {
                Uri origin;
                if (!Uri.TryCreate(relay, UriKind.Absolute, out origin) ||
                    origin.Scheme != Uri.UriSchemeHttps ||
                    !origin.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
                    origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
                    origin.Query.Length != 0 || origin.Fragment.Length != 0)
                    throw new InvalidOperationException(
                        "Invalid dedicated relay origin; startup refused.");
                return false; // PATHS requires the separate relay credential.
            }
            if (string.Equals(Environment.GetEnvironmentVariable("AEROHELM_REQUIRE_LOGIN"),
                              "1",StringComparison.Ordinal))
                return true;
            // Directly launching the compiled AeroHelm GCS must also require
            // login before exposing authenticated network workspaces.
            // The explicit --local-maintenance path above remains offline.
            string compiled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "plugins", "AeroHelm.Cloud.Plugin.Validation.dll");
            if (File.Exists(compiled)) return true;
            try
            {
                string branding=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logo.txt");
                if (!File.Exists(branding)) return false;
                string title = File.ReadAllText(branding).Trim();
                return title.Equals("Vihanga GCS", StringComparison.OrdinalIgnoreCase) ||
                       title.Equals("AeroHelm GCS", StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static bool Authenticate()
        {
            using (var form=new AeroHelmStartupWindow())
                return form.ShowDialog()==DialogResult.OK;
        }
    }

    internal sealed class AeroHelmStartupWindow : Form
    {
        private readonly TextBox origin = new TextBox();
        private readonly TextBox user = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly Button login = new Button();
        private readonly Button kerberos = new Button();
        private readonly Label status = new Label();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public AeroHelmStartupWindow()
        {
            Text="VIHANGA GCS — Cloud sign in";
            StartPosition=FormStartPosition.CenterScreen;
            Width=680;Height=325;
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=false;MinimizeBox=false;
            var panel=new TableLayoutPanel {
                Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=2,RowCount=5
            };
            panel.ColumnStyles.Add(new ColumnStyle(System.Windows.Forms.SizeType.Absolute,105));
            panel.ColumnStyles.Add(new ColumnStyle(System.Windows.Forms.SizeType.Percent,100));
            for(int i=0;i<3;i++)panel.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute,62));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            Controls.Add(panel);
            string cached=Environment.GetEnvironmentVariable("AEROHELM_CLOUD_URL");
            if (string.IsNullOrWhiteSpace(cached))
            {
                try {
                    string path=Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),"AeroHelm","cloud-url.txt");
                    if(File.Exists(path))cached=File.ReadAllText(path).Trim();
                }catch(IOException){}catch(UnauthorizedAccessException){}
            }
            origin.Text=string.IsNullOrWhiteSpace(cached)?"https://":cached;
            user.Text="operator";
            password.UseSystemPasswordChar=true;
            origin.Dock=DockStyle.Fill;user.Dock=DockStyle.Fill;password.Dock=DockStyle.Fill;
            Add(panel,0,"Cloud URL",origin);
            Add(panel,1,"Username",user);
            Add(panel,2,"Password",password);
            login.Text="Sign in to VIHANGA";
            login.AutoSize=true;
            login.Click+=async(s,e)=>await Login();
            kerberos.Text="Windows SSO (Kerberos)";
            kerberos.AutoSize=true;
            // Only expose the enterprise SSO control when explicitly opted-in.
            // A real KDC, HTTP service keytab and private Negotiate gateway
            // must exist; the ordinary operator password login remains usable.
            kerberos.Visible=string.Equals(Environment.GetEnvironmentVariable(
                "AEROHELM_KERBEROS_UI_ENABLED"),"1",StringComparison.Ordinal);
            kerberos.Click+=async(s,e)=>await KerberosLogin();
            var cancel=new Button{Text="Exit",DialogResult=DialogResult.Cancel};
            var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};
            actions.Controls.Add(login);
            actions.Controls.Add(kerberos);
            actions.Controls.Add(cancel);
            panel.Controls.Add(actions,1,3);
            status.Text="Cloud login controls access to cloud services. For offline USB maintenance use --local-maintenance; this is not a flight authorization.";
            status.Dock=DockStyle.Fill;
            status.AutoEllipsis=true;
            panel.Controls.Add(status,0,4);panel.SetColumnSpan(status,2);
            AcceptButton=login;CancelButton=cancel;
            // Display a real, keyboard-accessible modal sign-in as the first
            // focused AeroHelm window. Splash is hidden by Program.Main
            // throughout authentication, including network-await periods.
            ShowInTaskbar=true;
            Shown+=(s,e)=>BeginInvoke((Action)(()=>{
                if(!IsDisposed && !Disposing)
                {
                    Activate();
                    password.Focus();
                }
            }));
        }

        private static void Add(TableLayoutPanel panel,int row,string caption,Control input)
        {
            panel.Controls.Add(new Label{Text=caption,TextAlign=System.Drawing.ContentAlignment.MiddleLeft,
                                        Dock=DockStyle.Fill},0,row);
            panel.Controls.Add(input,1,row);
        }

        private static string LoginRequest(string origin,string username,string secret)
        {
            var uri=new Uri(new Uri(origin.TrimEnd('/')+"/"),"api/v1/auth/login");
            var payload=new JavaScriptSerializer().Serialize(new{username=username,password=secret});
            byte[] data=Encoding.UTF8.GetBytes(payload);
            var req=(HttpWebRequest)WebRequest.Create(uri);
            req.Method="POST";req.Timeout=12000;req.ReadWriteTimeout=12000;
            req.AllowAutoRedirect=false;
            req.ContentType="application/json";req.ContentLength=data.Length;
            using(var stream=req.GetRequestStream())stream.Write(data,0,data.Length);
            using(var response=(HttpWebResponse)req.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))
            {
                if(response.StatusCode!=HttpStatusCode.OK)
                    throw new InvalidOperationException("Cloud login rejected");
                return reader.ReadToEnd();
            }
        }

        private static string NegotiateRequest(string origin)
        {
            // Windows credentials are offered ONLY to a trusted private HTTPS
            // origin, and ONLY through Negotiate (never Basic authentication).
            var url=new Uri(new Uri(origin.TrimEnd('/')+"/"),"sso/kerberos");
            var cache=new CredentialCache();
            cache.Add(url,"Negotiate",CredentialCache.DefaultNetworkCredentials);
            var req=(HttpWebRequest)WebRequest.Create(url);
            req.Method="POST";
            req.Timeout=12000;req.ReadWriteTimeout=12000;
            req.AllowAutoRedirect=false;
            req.Credentials=cache;
            req.PreAuthenticate=false;
            req.ContentLength=0;
            using(var response=(HttpWebResponse)req.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))
            {
                if(response.StatusCode!=HttpStatusCode.OK)
                    throw new InvalidOperationException("Enterprise SSO was not accepted");
                return reader.ReadToEnd();
            }
        }

        private Uri CheckedCloudOrigin()
        {
            Uri url;
            if(!Uri.TryCreate(origin.Text.Trim(),UriKind.Absolute,out url) ||
               !(url.Scheme==Uri.UriSchemeHttps &&
                 (url.Host.EndsWith(".ts.net",StringComparison.OrdinalIgnoreCase) ||
                  url.IsLoopback)) ||
               !string.IsNullOrEmpty(url.UserInfo)||url.AbsolutePath!="/"||
               !string.IsNullOrEmpty(url.Query)||!string.IsNullOrEmpty(url.Fragment))
                throw new InvalidOperationException("Enter a trusted private HTTPS cloud origin.");
            return url;
        }

        private void AcceptToken(string reply,string address)
        {
            var result=json.Deserialize<CloudLoginResponse>(reply);
            if(result==null || string.IsNullOrEmpty(result.access_token) ||
               result.expires_in<=0 || result.expires_in>3600)
                throw new InvalidOperationException("Invalid cloud authentication response");
            Program.AeroHelmOperatorToken=result.access_token;
            Program.AeroHelmCloudOrigin=address;
            Program.AeroHelmTokenExpiresUtc=DateTime.UtcNow.AddSeconds(
                Math.Max(1,result.expires_in-30));
            DialogResult=DialogResult.OK;
            Close();
        }

        private async Task KerberosLogin()
        {
            if(!kerberos.Visible)return;
            kerberos.Enabled=false;login.Enabled=false;
            status.Text="Requesting Windows Kerberos SSO via private HTTPS...";
            try
            {
                string address=CheckedCloudOrigin().GetLeftPart(UriPartial.Authority);
                // Unlike a password login, Windows Negotiate may present a
                // domain credential/TGT. It MUST be pinned to a separately
                // administrator-approved exact HTTPS origin, never any *.ts.net.
                string trusted=Environment.GetEnvironmentVariable(
                    "AEROHELM_KERBEROS_SSO_ORIGIN") ?? "";
                Uri approved;
                if(!Uri.TryCreate(trusted,UriKind.Absolute,out approved) ||
                   approved.Scheme!=Uri.UriSchemeHttps ||
                   !(approved.Host.EndsWith(".ts.net",StringComparison.OrdinalIgnoreCase) ||
                     approved.Host.EndsWith(".internal",StringComparison.OrdinalIgnoreCase)) ||
                   approved.AbsolutePath!="/" || approved.UserInfo!="" ||
                   approved.Query!="" || approved.Fragment!="")
                    throw new InvalidOperationException(
                        "Kerberos SSO requires an exact administrator-pinned private HTTPS origin.");
                // KDC service principals may use a dedicated internal DNS
                // host+TLS port. Only Negotiate with the administrator-pinned
                // identity endpoint, never with an arbitrary cloud URL.
                string ssoOrigin=approved.GetLeftPart(UriPartial.Authority);
                // The SSO endpoint and token audience MUST be the exact
                // same private cloud origin. A different *.ts.net host could
                // otherwise mint a token for an unrelated AeroHelm server.
                if (!string.Equals(ssoOrigin,address,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Kerberos gateway must share the exact cloud HTTPS origin.");
                string reply=await Task.Run(()=>NegotiateRequest(ssoOrigin));
                if(!IsDisposed)AcceptToken(reply,address);
            }
            catch(WebException ex)
            {
                var http=ex.Response as HttpWebResponse;
                status.Text=http==null?"Kerberos gateway unavailable: "+ex.Status:
                    "Kerberos SSO rejected (HTTP "+(int)http.StatusCode+"). "+
                    "Confirm domain trust, gateway and KDC configuration.";
                if(http!=null)http.Dispose();
            }
            catch(Exception)
            {
                status.Text="Kerberos SSO unavailable. Check identity gateway; "+
                    "normal password login remains available.";
            }
            finally {if(!IsDisposed){kerberos.Enabled=true;login.Enabled=true;}}
        }

        private async Task Login()
        {
            login.Enabled=false;status.Text="Authenticating with private cloud...";
            string secret=null;
            try
            {
                Uri url=CheckedCloudOrigin();
                if(user.Text.Trim().Length==0||password.Text.Length==0)
                    throw new InvalidOperationException("Enter operator credentials.");
                var address=url.GetLeftPart(UriPartial.Authority);
                string username=user.Text.Trim();
                secret=password.Text;
                password.Clear();
                var reply=await Task.Run(()=>LoginRequest(address,username,secret));
                if(!IsDisposed)AcceptToken(reply,address);
            }
            catch(WebException ex) {
                var response=ex.Response as HttpWebResponse;
                status.Text=response!=null ?
                    "Cloud login rejected (HTTP "+(int)response.StatusCode+")." :
                    "Could not reach cloud: "+ex.Status;
            }
            catch(Exception ex) { status.Text=ex.Message; }
            finally { secret=null;login.Enabled=true; }
        }

        private sealed class CloudLoginResponse
        {
            public string access_token{get;set;}
            public int expires_in{get;set;}
        }
    }
}
