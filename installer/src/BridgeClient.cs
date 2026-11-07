// Talks to the bridge this window started, over 127.0.0.1 only. The bridge's self-signed certificate is pinned by
// the SHA-256 the bridge wrote into the status file, and the per-run desktop token is the only credential sent.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GTAVRidingCompanion
{
    sealed class ApiResult
    {
        public int Status;
        public Dictionary<string, object> Body;
        public string ErrorCode;
        public bool Ok { get { return Status >= 200 && Status < 300; } }
    }

    sealed class BridgeClient
    {
        static string pinnedCertificate;
        readonly string baseUrl;
        readonly string token;

        static BridgeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback += Validate;
        }

        BridgeClient(string baseUrl, string token)
        {
            this.baseUrl = baseUrl;
            this.token = token;
        }

        /// <summary>Client for the status file the bridge wrote, or null when it has no desktop token yet.</summary>
        internal static BridgeClient FromStatus(Dictionary<string, object> status)
        {
            string token = Json.Str(status, "desktopToken");
            int? port = Json.Int(status, "port");
            if (String.IsNullOrEmpty(token) || port == null) return null;
            bool https = Json.Str(status, "transport") != "http";
            pinnedCertificate = https ? Json.Str(status, "certSha256") : null;
            if (https && String.IsNullOrEmpty(pinnedCertificate)) return null;
            return new BridgeClient((https ? "https" : "http") + "://127.0.0.1:" + port.Value, token);
        }

        static bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            var request = sender as HttpWebRequest;
            if (request == null || !request.RequestUri.IsLoopback) return errors == SslPolicyErrors.None;
            string pin = pinnedCertificate;
            if (pin == null || certificate == null) return false;
            using (var sha = SHA256.Create())
                return String.Equals(BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-", ""), pin, StringComparison.OrdinalIgnoreCase);
        }

        internal Task<ApiResult> GetAsync(string path) { return Task.Run(() => Send("GET", path, null)); }

        internal Task<ApiResult> PostAsync(string path, object body) { return Task.Run(() => Send("POST", path, new JavaScriptSerializer().Serialize(body))); }

        ApiResult Send(string method, string path, string body)
        {
            var request = (HttpWebRequest)WebRequest.Create(baseUrl + path);
            request.Method = method;
            request.Timeout = 6000;
            request.ReadWriteTimeout = 6000;
            request.Headers["Authorization"] = "Bearer " + token;
            request.Accept = "application/json";
            request.Proxy = null;
            try
            {
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentType = "application/json";
                    request.ContentLength = bytes.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse()) return Read(response);
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null) using (response) return Read(response);
                return new ApiResult { Status = 0, ErrorCode = "BRIDGE_UNREACHABLE" };
            }
        }

        static ApiResult Read(HttpWebResponse response)
        {
            var result = new ApiResult { Status = (int)response.StatusCode };
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                string text = reader.ReadToEnd();
                if (text.Length > 0)
                {
                    try { result.Body = Json.Parse(text); }
                    catch (ArgumentException) { result.ErrorCode = "INVALID_RESPONSE"; }
                }
            }
            if (!result.Ok && result.Body != null) result.ErrorCode = Json.Str(Json.Obj(result.Body, "error"), "code") ?? "INTERNAL";
            return result;
        }
    }

    /// <summary>Small helpers over JavaScriptSerializer's dictionaries.</summary>
    static class Json
    {
        internal static Dictionary<string, object> Parse(string text)
        {
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(text);
        }

        internal static object Get(Dictionary<string, object> d, string key)
        {
            object value;
            return d != null && d.TryGetValue(key, out value) ? value : null;
        }

        internal static Dictionary<string, object> Obj(Dictionary<string, object> d, string key) { return Get(d, key) as Dictionary<string, object>; }

        internal static string Str(Dictionary<string, object> d, string key) { return Get(d, key) as string; }

        internal static bool? Bool(Dictionary<string, object> d, string key)
        {
            object value = Get(d, key);
            return value is bool ? (bool?)value : null;
        }

        internal static double? Num(Dictionary<string, object> d, string key)
        {
            object value = Get(d, key);
            if (value is int || value is long || value is decimal || value is double || value is float)
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return null;
        }

        internal static int? Int(Dictionary<string, object> d, string key)
        {
            double? value = Num(d, key);
            return value == null ? (int?)null : (int)Math.Round(value.Value);
        }

        internal static List<Dictionary<string, object>> List(Dictionary<string, object> d, string key)
        {
            var list = new List<Dictionary<string, object>>();
            var raw = Get(d, key) as IList;
            if (raw != null) foreach (object item in raw) { var dict = item as Dictionary<string, object>; if (dict != null) list.Add(dict); }
            return list;
        }
    }

    /// <summary>English texts for the bridge's reason codes (same wording as the Android app).</summary>
    static class Reasons
    {
        static readonly Dictionary<string, string> Texts = new Dictionary<string, string>
        {
            { "ADAPTER_ABSENT", "Start GTA V in Story Mode with GTBike V. The game adapter is not running yet." },
            { "ADAPTER_STALE", "The game has not sent new data recently (is it paused or minimized?)." },
            { "UNSUPPORTED_VERSION", "This GTBike V version is not supported yet." },
            { "MOD_TRANSITION_IN_PROGRESS", "GTBike V is starting or stopping. Wait a moment." },
            { "GAME_NOT_IN_CONTROL", "The game is paused or loading." },
            { "CAPABILITY_UNAVAILABLE", "Not available on this PC right now." },
            { "MOD_INACTIVE", "Activate GTBike V first." },
            { "MOD_NOT_LOADED", "GTBike V is not loaded in the game." },
            { "WORKOUT_PC_CONTROL_REQUIRED", "A workout is running. Manage it in the game." },
            { "PANEL_CLOSED", "Open the route selector in the game first." },
            { "RIDER_MOVING", "Stop riding first." },
            { "AUTOPILOT_ON", "Not available while autopilot is on." },
            { "REVISION_CONFLICT", "Something changed in the game. Review the current values and try again." },
            { "INSTANCE_CHANGED", "The game adapter restarted. Try again." },
            { "VALUE_OUT_OF_RANGE", "Value out of range." },
            { "VALUE_NOT_ALLOWED", "That option is not available right now." },
            { "BRIDGE_BUSY", "The bridge is handling another request. Try again in a moment." },
            { "OPERATION_UNKNOWN", "No confirmation from the game. Check the game before trying again." },
            { "BRIDGE_UNREACHABLE", "The bridge is not answering. Start it from the Phone tab." },
            { "AUTH_REQUIRED", "The bridge did not accept this window. Restart the bridge." },
            { "FORBIDDEN_SCOPE", "Not allowed." },
            { "NOT_FOUND", "Not found." },
            { "RIDER_NOT_ON_BIKE", "Get on a bicycle in the game first." },
            { "ADDON_BUSY", "Another add-on is running. Stop it first." },
            { "ADDON_NO_POINTS", "No Downtown street points were found. Ride closer to Downtown Los Santos and try again." },
            { "INTERNAL", "Something went wrong in the game or the bridge. Try again." },
        };

        internal static string Text(string code)
        {
            string text;
            return code != null && Texts.TryGetValue(code, out text) ? text : "Not available right now" + (code == null ? "." : " (" + code + ").");
        }
    }
}
