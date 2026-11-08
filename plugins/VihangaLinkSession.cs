// Separate private telemetry relay authentication for Vihanga.
// Never overwrite Oracle M1 login credentials or persist relay access tokens.
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace AeroHelm.GcsCloud
{
    internal static class VihangaLinkSession
    {
        private static readonly object Sync = new object();
        private static string relayToken;
        private static string relayOrigin;
        private static DateTime relayExpiresUtc = DateTime.MinValue;

        internal static bool UsesDedicatedRelay
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    "VIHANGA_LINK_ORIGIN"));
            }
        }

        internal static string TargetOrigin
        {
            get
            {
                if (!UsesDedicatedRelay) return MissionPlanner.Program.AeroHelmCloudOrigin;
                string raw = Environment.GetEnvironmentVariable("VIHANGA_LINK_ORIGIN");
                Uri parsed;
                if (!Uri.TryCreate(raw, UriKind.Absolute, out parsed) ||
                    parsed.Scheme != Uri.UriSchemeHttps ||
                    !parsed.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ||
                    parsed.UserInfo.Length > 0 ||
                    parsed.AbsolutePath != "/" || parsed.Query.Length > 0 ||
                    parsed.Fragment.Length > 0)
                    throw new InvalidOperationException(
                        "VIHANGA_LINK_ORIGIN must be an exact private Tailscale HTTPS origin.");
                return parsed.GetLeftPart(UriPartial.Authority);
            }
        }

        internal static DateTime ExpiresUtc
        {
            get
            {
                if (!UsesDedicatedRelay)
                    return MissionPlanner.Program.AeroHelmTokenExpiresUtc;
                lock (Sync) return relayExpiresUtc;
            }
        }

        internal static string AccessToken
        {
            get
            {
                if (!UsesDedicatedRelay)
                    return MissionPlanner.Program.AeroHelmOperatorToken;
                lock (Sync)
                {
                    if (relayOrigin != TargetOrigin ||
                        DateTime.UtcNow >= relayExpiresUtc)
                        return null;
                    return relayToken;
                }
            }
        }

        internal static string CloudOrigin
        {
            get
            {
                if (!UsesDedicatedRelay)
                    return MissionPlanner.Program.AeroHelmCloudOrigin;
                return TargetOrigin;
            }
        }

        internal static bool Authenticated
        {
            get { return !string.IsNullOrEmpty(AccessToken) &&
                         DateTime.UtcNow < ExpiresUtc; }
        }

        internal static void Accept(string origin, string token, int expiresIn)
        {
            if (!UsesDedicatedRelay)
                throw new InvalidOperationException("No dedicated relay configured.");
            if (!string.Equals(origin, TargetOrigin, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(token) || expiresIn < 30 || expiresIn > 3600)
                throw new InvalidOperationException("Invalid dedicated relay authentication.");
            lock (Sync)
            {
                relayOrigin = origin;
                relayToken = token;
                relayExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn - 15);
            }
        }

        // Verify the new Oracle VM *before* sending operator credentials.
        // OCID is an infrastructure identity, not a substitute for WSS/TLS.
        internal static void VerifyOracleTargetIdentity()
        {
            string expected = Environment.GetEnvironmentVariable(
                "VIHANGA_EXPECTED_ORACLE_INSTANCE_OCID");
            if (string.IsNullOrWhiteSpace(expected)) return;
            if (!UsesDedicatedRelay)
                throw new InvalidOperationException(
                    "Oracle instance binding requires an explicit private relay origin.");
            if (!expected.StartsWith("ocid1.instance.oc", StringComparison.Ordinal) ||
                expected.Length < 40 ||
                !System.Text.RegularExpressions.Regex.IsMatch(expected,
                    @"^[A-Za-z0-9_.-]+$"))
                throw new InvalidOperationException("Invalid expected Oracle instance OCID.");

            var request = (HttpWebRequest)WebRequest.Create(
                new Uri(new Uri(TargetOrigin + "/"), "health"));
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.Timeout = 5000;
            request.ReadWriteTimeout = 5000;
            request.Accept = "application/json";
            string responseText;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("Private relay identity endpoint unavailable.");
                responseText = reader.ReadToEnd();
            }
            var payload = new JavaScriptSerializer()
                .Deserialize<System.Collections.Generic.Dictionary<string,object>>(responseText);
            object detail;
            var verified = payload != null && payload.TryGetValue("oracle_instance", out detail) ?
                detail as System.Collections.Generic.Dictionary<string,object> : null;
            object ready, configured, digest;
            if (verified == null || !verified.TryGetValue("configured", out configured) ||
                !verified.TryGetValue("verified", out ready) ||
                !verified.TryGetValue("instance_sha256", out digest) ||
                !(configured is bool) || !(bool)configured ||
                !(ready is bool) || !(bool)ready)
                throw new InvalidOperationException(
                    "Oracle instance identity was not verified by the private relay.");
            string expectedDigest;
            using (var hasher = SHA256.Create())
                expectedDigest = BitConverter.ToString(hasher.ComputeHash(
                    Encoding.ASCII.GetBytes(expected))).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(Convert.ToString(digest), expectedDigest,
                               StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Private relay belongs to a different Oracle compute instance.");
        }

        // Parameter inspection is a distinct authenticated GET-only operation.
        // It never opens an FC write path or redirects a bearer token.
        internal static string ReadParameters(string vehicleId, bool values = false)
        {
            if (!UsesDedicatedRelay || !Authenticated)
                throw new InvalidOperationException("Private telemetry relay sign-in required.");
            if (string.IsNullOrWhiteSpace(vehicleId) || vehicleId.Length > 64)
                throw new ArgumentException("Invalid aircraft identifier.");
            foreach (char ch in vehicleId)
            {
                if (!(char.IsLetterOrDigit(ch) && ch <= 127) &&
                    ch != '-' && ch != '_')
                    throw new ArgumentException("Invalid aircraft identifier.");
            }
            string destination = TargetOrigin;
            string token = AccessToken;
            var path = "api/v1/vehicles/" + Uri.EscapeDataString(vehicleId) +
                       "/parameters" + (values ? "/values" : "");
            var uri = new Uri(new Uri(destination + "/"), path);
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("Unexpected private snapshot response.");
                return reader.ReadToEnd();
            }
        }

        // Explicit telemetry-request duplex. No arbitrary Mission Planner
        // MAVLink, vehicle command or parameter write crosses this API.
        // The relay and physically disarmed Pi independently enforce policy.
        internal static string RequestDisarmedTelemetry(string vehicleId)
        {
            if (!UsesDedicatedRelay || !Authenticated)
                throw new InvalidOperationException("Dedicated telemetry relay sign-in required.");
            if (string.IsNullOrWhiteSpace(vehicleId) || vehicleId.Length > 64)
                throw new ArgumentException("Invalid aircraft identifier.");
            foreach (char ch in vehicleId)
            {
                if (!(ch <= 127 && char.IsLetterOrDigit(ch)) &&
                    ch != '-' && ch != '_')
                    throw new ArgumentException("Invalid aircraft identifier.");
            }
            Uri target = new Uri(new Uri(TargetOrigin + "/"),
                "api/v1/vehicles/" + Uri.EscapeDataString(vehicleId) +
                "/telemetry/probe");
            var request = (HttpWebRequest)WebRequest.Create(target);
            request.Method = "POST";
            request.AllowAutoRedirect = false;
            request.Timeout = 6000;
            request.ReadWriteTimeout = 6000;
            request.ContentLength = 0;
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] =
                "Bearer " + AccessToken;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("Telemetry probe rejected.");
                // QUEUED is not confirmation of real ATTITUDE delivery.
                return reader.ReadToEnd();
            }
        }

        internal static void Clear()
        {
            lock (Sync)
            {
                relayToken = null;
                relayOrigin = null;
                relayExpiresUtc = DateTime.MinValue;
            }
        }
    }
}
