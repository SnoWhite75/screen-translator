// ScreenTranslator.cs - 화면 자동 번역 오버레이 (C# 5, Windows 내장 csc.exe 로 빌드)
// Windows 10/11 내장 OCR(Windows.Media.Ocr) + Google 번역(무료 웹 엔드포인트) -> 한국어
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using WMO = Windows.Media.Ocr;
using WGI = Windows.Graphics.Imaging;
using WSS = Windows.Storage.Streams;

namespace ScreenTranslatorApp
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        public const int WM_HOTKEY = 0x0312;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x20;
        public const int WS_EX_LAYERED = 0x80000;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const uint MOD_ALT = 1;
        public const uint MOD_CONTROL = 2;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    }

    // ------------------------------------------------------------------ settings
    class Settings
    {
        public int LiveIntervalMs = 1500;
        public string UpdateUrl = "https://raw.githubusercontent.com/SnoWhite75/screen-translator/main/";
        public bool AutoUpdate = true;
        public int FailedVersion = 0;
        public string DeepLKey = "";

        static string FilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTranslator");
            return Path.Combine(dir, "settings.ini");
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                string p = FilePath();
                if (File.Exists(p))
                {
                    foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim();
                        string v = line.Substring(i + 1).Trim();
                        switch (k)
                        {
                            case "live": int.TryParse(v, out s.LiveIntervalMs); break;
                            case "updateurl": if (v.Length > 0) s.UpdateUrl = v; break;
                            case "autoupdate": s.AutoUpdate = v == "1"; break;
                            case "failedver": int.TryParse(v, out s.FailedVersion); break;
                            case "deeplkey": s.DeepLKey = v; break;
                        }
                    }
                }
            }
            catch { }
            if (s.LiveIntervalMs < 700) s.LiveIntervalMs = 1500;
            return s;
        }

        public void Save()
        {
            try
            {
                string p = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                List<string> l = new List<string>();
                l.Add("live=" + LiveIntervalMs);
                l.Add("updateurl=" + UpdateUrl);
                l.Add("autoupdate=" + (AutoUpdate ? "1" : "0"));
                l.Add("failedver=" + FailedVersion);
                l.Add("deeplkey=" + DeepLKey);
                File.WriteAllLines(p, l.ToArray(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ http / json
    class Http
    {
        public int Status;
        public string Body = "";
        public string Error;
    }

    static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            return Val(s, ref i);
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object Val(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("eof");
            char c = s[i];
            if (c == '{')
            {
                i++;
                Dictionary<string, object> d = new Dictionary<string, object>();
                Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("colon");
                    i++;
                    d[k] = Val(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("object");
                }
            }
            if (c == '[')
            {
                i++;
                List<object> l = new List<object>();
                Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Val(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("array");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st0 = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == st0) throw new FormatException("token");
            return double.Parse(s.Substring(st0, i - st0), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("string");
            i++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
                    char n = s[i++];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    static class Fetcher
    {
        public static Http Send(string url)
        {
            return Request("GET", url, null, null, null, 15000);
        }

        public static Http Send(string url, int timeoutMs)
        {
            return Request("GET", url, null, null, null, timeoutMs);
        }

        public static Http Post(string url, string form, string authorization)
        {
            return Request("POST", url, form, "application/x-www-form-urlencoded; charset=UTF-8", authorization, 15000);
        }

        public static Http PostJson(string url, string json, string authorization)
        {
            return Request("POST", url, json, "application/json; charset=UTF-8", authorization, 15000);
        }

        static Http Request(string method, string url, string body, string contentType, string authorization, int timeoutMs)
        {
            Http res = new Http();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";
                req.ServicePoint.Expect100Continue = false;
                if (authorization != null) req.Headers["Authorization"] = authorization;
                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    req.ContentType = contentType;
                    req.ContentLength = data.Length;
                    using (Stream rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse()) Fill(res, resp);
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null) { using (resp) { Fill(res, resp); } }
                else res.Error = ex.Status + " - " + ex.Message;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
            }
            return res;
        }

        static void Fill(Http res, HttpWebResponse resp)
        {
            res.Status = (int)resp.StatusCode;
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) res.Body = sr.ReadToEnd();
        }
    }

    // ------------------------------------------------------------------ translation
    // 한 화면의 문단을 "요청 1번"으로 묶어서 보내고, 요청은 한 번에 하나씩만 보내요.
    // 그래도 제한(429)이 걸리면 잠시 쉬었다가 자동으로 다시 시도해요.
    static class Translator
    {
        static Dictionary<string, string> cache = new Dictionary<string, string>();
        static readonly object gate = new object();
        static DateTime nextAllowed = DateTime.MinValue;

        // Google (무료, 비공식)
        static DateTime googleBlockedUntil = DateTime.MinValue;
        static int strikes = 0;
        static bool lastGoogleMismatch = false;
        static string lastGoogleError = "";

        // Microsoft (Edge 번역, 무료)
        static string msToken = "";
        static DateTime msTokenExpires = DateTime.MinValue;
        static DateTime msBlockedUntil = DateTime.MinValue;
        static string lastMsError = "";

        // Lingva (Google 번역을 대신 요청해 주는 공개 서버, 무료)
        static DateTime lingvaBlockedUntil = DateTime.MinValue;
        static string lastLingvaError = "";

        // DeepL (선택, 무료 API 키)
        static string lastDeepLError = "";

        public static string LastError = "";
        public static string DeepLKey = "";
        public static string Engine = "";

        public static double HangulRatio(string s)
        {
            int letters = 0, han = 0;
            foreach (char c in s)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if ((c >= 0xAC00 && c <= 0xD7A3) || (c >= 0x3131 && c <= 0x318E)) han++;
            }
            return letters == 0 ? 0 : (double)han / letters;
        }

        public static bool WorthTranslating(string s)
        {
            bool letter = false;
            foreach (char c in s) if (char.IsLetter(c)) { letter = true; break; }
            if (!letter) return false;
            return HangulRatio(s) < 0.5;
        }

        // 쓸 수 있는 엔진이 하나도 없을 때만 0보다 커요 (남은 초)
        public static int CooldownSeconds()
        {
            if (DeepLKey.Length > 0) return 0;
            double g = (googleBlockedUntil - DateTime.UtcNow).TotalSeconds;
            double m = (msBlockedUntil - DateTime.UtcNow).TotalSeconds;
            double lg = (lingvaBlockedUntil - DateTime.UtcNow).TotalSeconds;
            double s = Math.Min(Math.Min(g, m), lg);
            return s > 0 ? (int)Math.Ceiling(s) : 0;
        }

        public static void ResetCooldown()
        {
            googleBlockedUntil = DateTime.MinValue;
            msBlockedUntil = DateTime.MinValue;
            lingvaBlockedUntil = DateTime.MinValue;
            msToken = "";
            strikes = 0;
        }

        static void Throttle(int gapMs)
        {
            DateTime now = DateTime.UtcNow;
            if (nextAllowed > now) Thread.Sleep((int)(nextAllowed - now).TotalMilliseconds);
            nextAllowed = DateTime.UtcNow.AddMilliseconds(gapMs);
        }

        static void Log(string s)
        {
            try
            {
                string p = Path.Combine(AppInfo.DataDir(), "translate.log");
                if (File.Exists(p) && new FileInfo(p).Length > 200000) File.Delete(p);
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + s + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        // 번역이 필요 없거나 실패한 항목은 null. 실패 이유는 LastError 에 남아요.
        public static string[] ToKorean(List<string> texts)
        {
            string[] result = new string[texts.Count];
            List<int> miss = new List<int>();
            lock (cache)
            {
                for (int i = 0; i < texts.Count; i++)
                {
                    string hit;
                    if (cache.TryGetValue(texts[i], out hit)) result[i] = hit.Length == 0 ? null : hit;
                    else miss.Add(i);
                }
            }
            if (miss.Count == 0) { LastError = ""; return result; }

            lock (gate)
            {
                int pos = 0;
                while (pos < miss.Count)
                {
                    List<int> idx = new List<int>();
                    int chars = 0;
                    while (pos < miss.Count)
                    {
                        int len = texts[miss[pos]].Length + 1;
                        if (idx.Count > 0 && (chars + len > 3500 || idx.Count >= 40)) break;
                        idx.Add(miss[pos]);
                        chars += len;
                        pos++;
                    }
                    List<string> part = new List<string>();
                    foreach (int i in idx) part.Add(texts[i]);
                    string[] outp = TranslateChunk(part);
                    if (outp == null) return result; // 실패: 지금까지 된 것만 돌려줘요 (이유는 LastError)
                    lock (cache)
                    {
                        if (cache.Count > 3000) cache.Clear();
                        for (int k = 0; k < idx.Count; k++)
                        {
                            string o = outp[k] ?? "";
                            if (string.Equals(o.Trim(), texts[idx[k]].Trim(), StringComparison.OrdinalIgnoreCase)) o = "";
                            cache[texts[idx[k]]] = o;
                            result[idx[k]] = o.Length == 0 ? null : o;
                        }
                    }
                }
            }
            LastError = "";
            return result;
        }

        // 엔진 순서: DeepL(키가 있을 때) → Microsoft → Google. 하나라도 되면 끝.
        static string[] TranslateChunk(List<string> part)
        {
            string[] r;
            List<string> errs = new List<string>();
            if (DeepLKey.Length > 0)
            {
                r = ViaDeepL(part);
                if (r != null) { Engine = "DeepL"; LastError = ""; return r; }
                errs.Add("DeepL: " + lastDeepLError);
            }
            r = ViaMicrosoft(part);
            if (r != null) { Engine = "Microsoft"; LastError = ""; return r; }
            errs.Add("Microsoft: " + lastMsError);

            int gcd = (int)Math.Ceiling((googleBlockedUntil - DateTime.UtcNow).TotalSeconds);
            if (gcd > 0)
            {
                lastGoogleError = "요청 제한으로 쉬는 중 (" + gcd + "초)";
            }
            else
            {
                r = ViaGoogle(part);
                if (r != null) { Engine = "Google"; LastError = ""; return r; }
            }
            errs.Add("Google: " + lastGoogleError);

            r = ViaLingva(part);
            if (r != null) { Engine = "Lingva"; LastError = ""; return r; }
            errs.Add("Lingva: " + lastLingvaError);

            LastError = "모든 번역 엔진이 실패했어요 - " + string.Join(" / ", errs.ToArray());
            Log(LastError);
            return null;
        }

        // ---------- Microsoft (Edge 번역) ----------
        static string JsonStr(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static string[] ViaMicrosoft(List<string> part)
        {
            if (DateTime.UtcNow < msBlockedUntil)
            {
                lastMsError = "잠시 쉬는 중 (" + (int)Math.Ceiling((msBlockedUntil - DateTime.UtcNow).TotalSeconds) + "초)";
                return null;
            }
            string err = "알 수 없는 오류";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (msToken.Length == 0 || DateTime.UtcNow >= msTokenExpires)
                {
                    Http a = Fetcher.Send("https://edge.microsoft.com/translate/auth");
                    if (a.Error != null) { err = "인증 서버 연결 실패 (" + a.Error + ")"; break; }
                    if (a.Status != 200 || a.Body.Trim().Length < 20) { err = "인증 실패 (HTTP " + a.Status + ")"; break; }
                    msToken = a.Body.Trim();
                    msTokenExpires = DateTime.UtcNow.AddMinutes(8);
                }
                StringBuilder sb = new StringBuilder("[");
                for (int i = 0; i < part.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"Text\":").Append(JsonStr(part[i])).Append('}');
                }
                sb.Append(']');
                Throttle(250);
                Http r = null;
                string[] msHosts = { "api-edge.cognitive.microsofttranslator.com", "api.cognitive.microsofttranslator.com" };
                foreach (string mh in msHosts)
                {
                    r = Fetcher.PostJson("https://" + mh + "/translate?to=ko&api-version=3.0&includeSentenceLength=true",
                                         sb.ToString(), "Bearer " + msToken);
                    if (r.Error == null) break;
                }
                if (r.Error != null) { err = "번역 서버 연결 실패 (" + r.Error + ")"; break; }
                if (r.Status == 401) { msToken = ""; err = "인증이 만료됐어요 (HTTP 401)"; continue; }
                if (r.Status == 429)
                {
                    msBlockedUntil = DateTime.UtcNow.AddSeconds(90);
                    lastMsError = "요청 제한 (HTTP 429)";
                    return null;
                }
                if (r.Status != 200) { err = "서버 오류 (HTTP " + r.Status + ")"; break; }
                try
                {
                    List<object> root = MiniJson.Parse(r.Body) as List<object>;
                    if (root == null || root.Count != part.Count) { err = "응답 개수가 맞지 않아요"; break; }
                    string[] res = new string[part.Count];
                    for (int i = 0; i < root.Count; i++)
                    {
                        Dictionary<string, object> d = root[i] as Dictionary<string, object>;
                        List<object> tr = d["translations"] as List<object>;
                        Dictionary<string, object> t0 = tr[0] as Dictionary<string, object>;
                        string lang = "";
                        if (d.ContainsKey("detectedLanguage"))
                        {
                            Dictionary<string, object> dl = d["detectedLanguage"] as Dictionary<string, object>;
                            if (dl != null && dl.ContainsKey("language")) lang = (dl["language"] as string) ?? "";
                        }
                        res[i] = (lang == "ko") ? "" : ((t0["text"] as string) ?? "").Trim();
                    }
                    lastMsError = "";
                    return res;
                }
                catch { err = "응답을 해석하지 못했어요"; break; }
            }
            msBlockedUntil = DateTime.UtcNow.AddSeconds(45);
            lastMsError = err;
            Log("Microsoft: " + err);
            return null;
        }

        // ---------- Google (무료, 비공식) ----------
        static string[] ViaGoogle(List<string> part)
        {
            string joined = string.Join("\n", part.ToArray());
            string[] r = GoogleRequest(joined, part.Count);
            if (r != null || part.Count == 1 || !lastGoogleMismatch) return r;
            // 줄 수가 맞지 않으면 한 줄씩 (천천히) 다시 해봐요
            string[] single = new string[part.Count];
            for (int i = 0; i < part.Count; i++)
            {
                string[] one = GoogleRequest(part[i], 1);
                if (one == null) return null;
                single[i] = one[0];
            }
            return single;
        }

        static string[] GoogleRequest(string text, int expect)
        {
            lastGoogleMismatch = false;
            string baseUrl = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ko&dt=t";
            string enc = Uri.EscapeDataString(text);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Throttle(600);
                Http r = enc.Length <= 1500 ? Fetcher.Send(baseUrl + "&q=" + enc) : Fetcher.Post(baseUrl, "q=" + enc, null);
                if (r.Error != null) { lastGoogleError = "인터넷에 연결할 수 없어요"; Thread.Sleep(300); continue; }
                if (r.Status == 429 || r.Status == 503)
                {
                    strikes++;
                    int wait = (int)(20 * Math.Pow(2, Math.Min(strikes, 5) - 1)); // 20초 → 40 → 80 → 160 → 320초
                    googleBlockedUntil = DateTime.UtcNow.AddSeconds(wait);
                    lastGoogleError = "요청 제한 (HTTP " + r.Status + ", " + wait + "초 쉬어요)";
                    Log("Google: " + lastGoogleError);
                    return null;
                }
                if (r.Status != 200) { lastGoogleError = "서버 오류 (HTTP " + r.Status + ")"; Thread.Sleep(300); continue; }
                try
                {
                    List<object> root = MiniJson.Parse(r.Body) as List<object>;
                    List<object> segs = root[0] as List<object>;
                    StringBuilder sb = new StringBuilder();
                    foreach (object o in segs)
                    {
                        List<object> seg = o as List<object>;
                        if (seg != null && seg.Count > 0 && seg[0] is string) sb.Append((string)seg[0]);
                    }
                    string all = sb.ToString().Replace("\r", "");
                    strikes = 0;
                    lastGoogleError = "";
                    if (expect == 1) return new string[] { all.Replace("\n", " ").Trim() };
                    List<string> ls = new List<string>(all.Split('\n'));
                    while (ls.Count > expect && ls[ls.Count - 1].Trim().Length == 0) ls.RemoveAt(ls.Count - 1);
                    if (ls.Count != expect)
                    {
                        lastGoogleMismatch = true;
                        lastGoogleError = "번역 결과의 줄 수가 맞지 않아요";
                        return null;
                    }
                    for (int i = 0; i < ls.Count; i++) ls[i] = ls[i].Trim();
                    return ls.ToArray();
                }
                catch
                {
                    lastGoogleError = "응답을 해석하지 못했어요";
                    return null;
                }
            }
            return null;
        }

        // ---------- Lingva (Google 프록시, 마지막 대체) ----------
        static string[] ViaLingva(List<string> part)
        {
            if (DateTime.UtcNow < lingvaBlockedUntil)
            {
                lastLingvaError = "잠시 쉬는 중";
                return null;
            }
            string joined = string.Join("\n", part.ToArray());
            string enc = Uri.EscapeDataString(joined);
            if (enc.Length > 1800) { lastLingvaError = "문장이 너무 길어요"; return null; }
            string err = "알 수 없는 오류";
            string[] hosts = { "https://lingva.ml", "https://lingva.lunar.icu" };
            foreach (string h in hosts)
            {
                Throttle(300);
                Http r = Fetcher.Send(h + "/api/v1/auto/ko/" + enc, 8000);
                if (r.Error != null) { err = "연결 실패 (" + r.Error + ")"; continue; }
                if (r.Status != 200) { err = "서버 오류 (HTTP " + r.Status + ")"; continue; }
                try
                {
                    Dictionary<string, object> d = MiniJson.Parse(r.Body) as Dictionary<string, object>;
                    string all = ((d["translation"] as string) ?? "").Replace("\r", "");
                    if (part.Count == 1)
                    {
                        lastLingvaError = "";
                        return new string[] { all.Replace("\n", " ").Trim() };
                    }
                    List<string> ls = new List<string>(all.Split('\n'));
                    while (ls.Count > part.Count && ls[ls.Count - 1].Trim().Length == 0) ls.RemoveAt(ls.Count - 1);
                    if (ls.Count != part.Count) { err = "번역 결과의 줄 수가 맞지 않아요"; continue; }
                    for (int i = 0; i < ls.Count; i++) ls[i] = ls[i].Trim();
                    lastLingvaError = "";
                    return ls.ToArray();
                }
                catch { err = "응답을 해석하지 못했어요"; }
            }
            lingvaBlockedUntil = DateTime.UtcNow.AddSeconds(120);
            lastLingvaError = err;
            Log("Lingva: " + err);
            return null;
        }

        // ---------- DeepL (선택, 무료 API 키) ----------
        static string[] ViaDeepL(List<string> part)
        {
            string host = DeepLKey.EndsWith(":fx") ? "https://api-free.deepl.com" : "https://api.deepl.com";
            StringBuilder body = new StringBuilder("target_lang=KO");
            foreach (string t in part) body.Append("&text=").Append(Uri.EscapeDataString(t));
            Throttle(250);
            Http r = Fetcher.Post(host + "/v2/translate", body.ToString(), "DeepL-Auth-Key " + DeepLKey);
            string err = null;
            if (r.Error != null) err = "DeepL 서버에 연결하지 못했어요";
            else if (r.Status == 403) err = "DeepL 키가 올바르지 않아요";
            else if (r.Status == 456) err = "DeepL 이번 달 무료 한도를 모두 썼어요";
            else if (r.Status == 429) err = "DeepL 요청이 너무 많아요";
            else if (r.Status != 200) err = "DeepL 서버 오류 (HTTP " + r.Status + ")";
            if (err == null)
            {
                try
                {
                    Dictionary<string, object> root = MiniJson.Parse(r.Body) as Dictionary<string, object>;
                    List<object> list = root["translations"] as List<object>;
                    if (list.Count == part.Count)
                    {
                        string[] res = new string[part.Count];
                        for (int i = 0; i < list.Count; i++)
                        {
                            Dictionary<string, object> d = list[i] as Dictionary<string, object>;
                            string lang = d.ContainsKey("detected_source_language") ? ((d["detected_source_language"] as string) ?? "") : "";
                            res[i] = (lang == "KO") ? "" : ((d["text"] as string) ?? "").Trim();
                        }
                        lastDeepLError = "";
                        return res;
                    }
                    err = "DeepL 응답의 개수가 맞지 않아요";
                }
                catch { err = "DeepL 응답을 해석하지 못했어요"; }
            }
            lastDeepLError = err;
            Log("DeepL: " + err);
            return null;
        }

        // 설정 화면의 "번역 테스트": 엔진마다 따로 실제로 요청해서 결과/이유를 보여줘요
        public static string Test(out bool ok)
        {
            ok = false;
            lock (gate)
            {
                List<string> sample = new List<string>(new string[] { "Hello, world! This is a translation test." });
                StringBuilder sb = new StringBuilder();
                string[] r;

                if (DeepLKey.Length > 0)
                {
                    r = ViaDeepL(sample);
                    if (r != null && r[0].Length > 0) { ok = true; sb.AppendLine("DeepL: 성공 → " + r[0]); }
                    else sb.AppendLine("DeepL: 실패 - " + lastDeepLError);
                }
                else sb.AppendLine("DeepL: 키 없음 (사용 안 함)");

                msBlockedUntil = DateTime.MinValue;
                msToken = "";
                r = ViaMicrosoft(sample);
                if (r != null && r[0].Length > 0) { ok = true; sb.AppendLine("Microsoft: 성공 → " + r[0]); }
                else sb.AppendLine("Microsoft: 실패 - " + lastMsError);

                googleBlockedUntil = DateTime.MinValue;
                r = ViaGoogle(sample);
                if (r != null && r[0].Length > 0) { ok = true; sb.AppendLine("Google: 성공 → " + r[0]); }
                else sb.AppendLine("Google: 실패 - " + lastGoogleError);

                lingvaBlockedUntil = DateTime.MinValue;
                r = ViaLingva(sample);
                if (r != null && r[0].Length > 0) { ok = true; sb.AppendLine("Lingva: 성공 → " + r[0]); }
                else sb.AppendLine("Lingva: 실패 - " + lastLingvaError);

                sb.AppendLine();
                sb.AppendLine(ok ? "하나라도 성공하면 번역은 동작해요. (자동으로 되는 엔진을 써요)" : "모든 엔진이 실패했어요. 인터넷 연결이나 방화벽을 확인해 주세요.");
                return sb.ToString();
            }
        }
    }

    // ------------------------------------------------------------------ OCR
    class LineInfo
    {
        public string Text;
        public RectangleF Rect;
    }

    class Item
    {
        public RectangleF Rect;
        public string Text;
        public int Lines = 1;
        public Color Bg, Fg;
    }

    static class OcrService
    {
        static Dictionary<string, WMO.OcrEngine> engines = new Dictionary<string, WMO.OcrEngine>();
        public static string BestLang = null;
        public static string Error = "";

        public static List<string> Languages()
        {
            List<string> l = new List<string>();
            try
            {
                foreach (Windows.Globalization.Language lg in WMO.OcrEngine.AvailableRecognizerLanguages) l.Add(lg.LanguageTag);
            }
            catch { }
            return l;
        }

        static WMO.OcrEngine Engine(string tag)
        {
            lock (engines)
            {
                WMO.OcrEngine e;
                if (engines.TryGetValue(tag, out e)) return e;
                try { e = WMO.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag)); }
                catch { e = null; }
                engines[tag] = e;
                return e;
            }
        }

        static readonly Regex CjkSpace = new Regex(
            @"(?<=[\u3000-\u30ff\u3400-\u9fff\uff00-\uffef]) +(?=[\u3000-\u30ff\u3400-\u9fff\uff00-\uffef])", RegexOptions.Compiled);

        // System.Runtime.WindowsRuntime.dll 의 AsTask() 대신 직접 완료를 기다려요 (참조 충돌 회피)
        static T Await<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            ManualResetEvent done = new ManualResetEvent(false);
            op.Completed = delegate (Windows.Foundation.IAsyncOperation<T> o, Windows.Foundation.AsyncStatus s) { done.Set(); };
            if (!done.WaitOne(20000)) throw new TimeoutException("OCR 시간 초과");
            return op.GetResults();
        }

        static List<LineInfo> Run(Bitmap bmp, WMO.OcrEngine eng, float factor)
        {
            List<LineInfo> list = new List<LineInfo>();
            using (WSS.DataWriter dw = new WSS.DataWriter())
            {
                // 비트맵 픽셀(BGRA)을 그대로 WinRT SoftwareBitmap 으로 넘겨요 (PNG 변환 없음)
                System.Drawing.Imaging.BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                byte[] raw = new byte[bd.Stride * bd.Height];
                int bw = bd.Width, bh = bd.Height;
                try { Marshal.Copy(bd.Scan0, raw, 0, raw.Length); }
                finally { bmp.UnlockBits(bd); }
                dw.WriteBytes(raw);
                WSS.IBuffer buf = dw.DetachBuffer();
                using (WGI.SoftwareBitmap sb = WGI.SoftwareBitmap.CreateCopyFromBuffer(buf, WGI.BitmapPixelFormat.Bgra8, bw, bh))
                {
                    WMO.OcrResult res = Await<WMO.OcrResult>(eng.RecognizeAsync(sb));
                    foreach (WMO.OcrLine ln in res.Lines)
                    {
                        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0;
                        foreach (WMO.OcrWord w in ln.Words)
                        {
                            Windows.Foundation.Rect r = w.BoundingRect;
                            if (r.X < x0) x0 = r.X;
                            if (r.Y < y0) y0 = r.Y;
                            if (r.X + r.Width > x1) x1 = r.X + r.Width;
                            if (r.Y + r.Height > y1) y1 = r.Y + r.Height;
                        }
                        if (x1 <= x0 || y1 <= y0) continue;
                        string t = CjkSpace.Replace(ln.Text, "").Trim();
                        if (t.Length == 0) continue;
                        LineInfo li = new LineInfo();
                        li.Text = t;
                        li.Rect = new RectangleF((float)(x0 / factor), (float)(y0 / factor), (float)((x1 - x0) / factor), (float)((y1 - y0) / factor));
                        list.Add(li);
                    }
                }
            }
            return list;
        }

        // detect: try every installed OCR language and keep the one that reads the most text
        public static List<LineInfo> Recognize(Bitmap src, bool detect)
        {
            Error = "";
            List<string> langs = Languages();
            if (langs.Count == 0)
            {
                Error = "OCR 언어팩이 없어요. 설정 > 시간 및 언어 > 언어 및 지역에서 언어를 추가하세요";
                return new List<LineInfo>();
            }
            int maxDim = 2000;
            try { maxDim = (int)WMO.OcrEngine.MaxImageDimension; } catch { }
            float factor = Math.Min(2f, (float)maxDim / Math.Max(src.Width, src.Height));
            Bitmap prep = src;
            bool own = false;
            if (Math.Abs(factor - 1f) > 0.01f)
            {
                prep = new Bitmap((int)(src.Width * factor), (int)(src.Height * factor));
                using (Graphics g = Graphics.FromImage(prep))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, 0, 0, prep.Width, prep.Height);
                }
                own = true;
            }
            try
            {
                List<string> tryTags = new List<string>();
                if (!detect && BestLang != null && langs.Contains(BestLang)) tryTags.Add(BestLang);
                else { foreach (string t in langs) { if (tryTags.Count < 6) tryTags.Add(t); } }

                List<LineInfo> best = new List<LineInfo>();
                int bestScore = -1;
                string bestTag = null;
                foreach (string tag in tryTags)
                {
                    WMO.OcrEngine eng = Engine(tag);
                    if (eng == null) continue;
                    List<LineInfo> l;
                    try { l = Run(prep, eng, factor); }
                    catch (Exception ex) { Error = "OCR 오류: " + ex.Message; continue; }
                    int score = 0;
                    foreach (LineInfo li in l) foreach (char c in li.Text) if (char.IsLetterOrDigit(c)) score++;
                    if (score > bestScore) { bestScore = score; best = l; bestTag = tag; }
                }
                if (bestTag != null && bestScore > 0) BestLang = bestTag;
                return best;
            }
            finally
            {
                if (own) prep.Dispose();
            }
        }
    }

    // ------------------------------------------------------------------ pipeline: bitmap -> translated items
    static class Pipeline
    {
        class Para
        {
            public string Text;
            public RectangleF Rect;
            public int Lines = 1;
            public double LastH;
        }

        static bool EndsSentence(string s)
        {
            if (s.Length == 0) return false;
            char c = s[s.Length - 1];
            return ".!?:;。！？：".IndexOf(c) >= 0;
        }

        static List<Para> Merge(List<LineInfo> lines)
        {
            List<Para> res = new List<Para>();
            Para cur = null;
            foreach (LineInfo li in lines)
            {
                bool join = false;
                if (cur != null)
                {
                    double h = li.Rect.Height;
                    double gap = li.Rect.Top - cur.Rect.Bottom;
                    double ratio = h > cur.LastH ? h / cur.LastH : cur.LastH / h;
                    join = !EndsSentence(cur.Text) && gap < 0.6 * h && gap > -0.5 * h &&
                           Math.Abs(li.Rect.Left - cur.Rect.Left) < 2.0 * h && ratio < 1.35;
                }
                if (join)
                {
                    bool cjk = li.Text.Length > 0 && li.Text[0] >= 0x3000;
                    cur.Text = cur.Text + (cjk ? "" : " ") + li.Text;
                    cur.Rect = RectangleF.Union(cur.Rect, li.Rect);
                    cur.Lines++;
                    cur.LastH = li.Rect.Height;
                }
                else
                {
                    cur = new Para();
                    cur.Text = li.Text;
                    cur.Rect = li.Rect;
                    cur.LastH = li.Rect.Height;
                    res.Add(cur);
                }
            }
            return res;
        }

        static Color Sample(Bitmap bmp, RectangleF r)
        {
            int[] xs = { (int)r.Left - 3, (int)r.Right + 3, (int)(r.Left + r.Width / 2), (int)(r.Left + r.Width / 2), (int)r.Left - 3, (int)r.Right + 3 };
            int[] ys = { (int)r.Top - 3, (int)r.Bottom + 3, (int)r.Top - 3, (int)r.Bottom + 3, (int)(r.Top + r.Height / 2), (int)(r.Top + r.Height / 2) };
            int sr = 0, sg = 0, sb = 0, n = 0;
            for (int i = 0; i < xs.Length; i++)
            {
                int x = Math.Max(0, Math.Min(bmp.Width - 1, xs[i]));
                int y = Math.Max(0, Math.Min(bmp.Height - 1, ys[i]));
                Color c = bmp.GetPixel(x, y);
                sr += c.R; sg += c.G; sb += c.B; n++;
            }
            return Color.FromArgb(sr / n, sg / n, sb / n);
        }

        public static List<Item> Process(Bitmap bmp, bool detect, out string error)
        {
            error = "";
            List<LineInfo> lines = OcrService.Recognize(bmp, detect);
            if (OcrService.Error.Length > 0 && lines.Count == 0) { error = OcrService.Error; return new List<Item>(); }
            List<Para> paras = Merge(lines);
            List<Para> todo = new List<Para>();
            foreach (Para p in paras) if (Translator.WorthTranslating(p.Text)) todo.Add(p);

            List<string> srcTexts = new List<string>();
            foreach (Para p in todo) srcTexts.Add(p.Text);
            string[] outText = todo.Count > 0 ? Translator.ToKorean(srcTexts) : new string[0];
            if (todo.Count > 0 && Translator.LastError.Length > 0) error = Translator.LastError;

            List<Item> items = new List<Item>();
            for (int i = 0; i < todo.Count; i++)
            {
                if (string.IsNullOrEmpty(outText[i])) continue;
                Item it = new Item();
                it.Rect = todo[i].Rect;
                it.Text = outText[i];
                it.Lines = todo[i].Lines;
                it.Bg = Sample(bmp, todo[i].Rect);
                double lum = 0.299 * it.Bg.R + 0.587 * it.Bg.G + 0.114 * it.Bg.B;
                it.Fg = lum > 140 ? Color.FromArgb(20, 20, 20) : Color.FromArgb(244, 244, 244);
                items.Add(it);
            }
            return items;
        }
    }

    // ------------------------------------------------------------------ region selection
    class SelectForm : Form
    {
        Bitmap shot;
        Rectangle vs;
        Point p0, p1;
        bool drag;
        public Rectangle Result = Rectangle.Empty;

        public SelectForm(Bitmap shot, Rectangle vs)
        {
            this.shot = shot;
            this.vs = vs;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
        }

        Rectangle Sel()
        {
            return new Rectangle(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Abs(p0.X - p1.X), Math.Abs(p0.Y - p1.Y));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.DrawImage(shot, 0, 0);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(b, ClientRectangle);
            using (Font f = new Font("Malgun Gothic", 14f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.DrawString("번역할 영역을 드래그하세요  (Esc: 취소)", f, Brushes.White, 20, 20);
            }
            if (drag)
            {
                Rectangle r = Sel();
                if (r.Width > 0 && r.Height > 0)
                {
                    g.DrawImage(shot, r, r, GraphicsUnit.Pixel);
                    using (Pen p = new Pen(Color.FromArgb(255, 98, 52), 2f)) g.DrawRectangle(p, r);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            drag = true; p0 = e.Location; p1 = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!drag) return;
            p1 = e.Location;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!drag) return;
            drag = false;
            p1 = e.Location;
            Rectangle r = Sel();
            r.Intersect(new Rectangle(0, 0, vs.Width, vs.Height));
            if (r.Width >= 20 && r.Height >= 12) Result = new Rectangle(vs.X + r.X, vs.Y + r.Y, r.Width, r.Height);
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Result = Rectangle.Empty; DialogResult = DialogResult.Cancel; Close(); }
            base.OnKeyDown(e);
        }
    }

    // ------------------------------------------------------------------ translated overlay
    class OverlayForm : Form
    {
        List<Item> items = new List<Item>();
        string status = "";
        bool clickThrough;
        public bool ExcludedFromCapture;
        public event Action Dismissed;

        public OverlayForm(Rectangle bounds, bool clickThrough)
        {
            this.clickThrough = clickThrough;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            BackColor = KeyColor;
            TransparencyKey = KeyColor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        static readonly Color KeyColor = Color.FromArgb(1, 2, 3);

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (clickThrough)
            {
                int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
                Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);
            }
            try { ExcludedFromCapture = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); }
            catch { ExcludedFromCapture = false; }
        }

        public void SetStatus(string st)
        {
            status = st;
            Invalidate();
        }

        public void SetContent(List<Item> list, string st)
        {
            items = list;
            status = st;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Dismissed != null) Dismissed();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(KeyColor); // 키 색상 = 투명, 번역 박스만 불투명하게 그려요
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            foreach (Item it in items)
            {
                RectangleF r = RectangleF.Inflate(it.Rect, 3f, 2f);
                float h = it.Rect.Height / Math.Max(1, it.Lines);
                float size = Math.Max(9f, Math.Min(h * 0.82f, 40f));
                using (SolidBrush bg = new SolidBrush(it.Bg))
                using (SolidBrush fg = new SolidBrush(it.Fg))
                {
                    while (true)
                    {
                        using (Font f = new Font("Malgun Gothic", size, FontStyle.Regular, GraphicsUnit.Pixel))
                        {
                            SizeF m = g.MeasureString(it.Text, f, (int)r.Width);
                            if (m.Height <= r.Height + 2 || size <= 9f)
                            {
                                RectangleF fill = new RectangleF(r.X, r.Y, r.Width, Math.Max(r.Height, m.Height + 2));
                                g.FillRectangle(bg, fill);
                                g.DrawString(it.Text, f, fg, new RectangleF(r.X, r.Y + 1, r.Width, fill.Height));
                                break;
                            }
                        }
                        size -= 1f;
                    }
                }
            }
            if (status.Length > 0)
            {
                using (Font f = new Font("Malgun Gothic", 12f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    SizeF m = g.MeasureString(status, f);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 20, 28, 36)))
                        g.FillRectangle(b, 4, 4, m.Width + 12, m.Height + 6);
                    g.DrawString(status, f, Brushes.White, 10, 7);
                }
            }
        }
    }

    // ------------------------------------------------------------------ app info / self update
    static class AppInfo
    {
        public const int AppVersion = 4;

        public static string DataDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTranslator");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public static string AppDir() { return Path.GetDirectoryName(Application.ExecutablePath); }
    }

    static class Updater
    {
        public static string NormalizeUrl(string u)
        {
            if (u == null) return "";
            u = u.Trim();
            if (u.Length == 0) return "";
            string gh = "https://github.com/";
            if (u.StartsWith(gh, StringComparison.OrdinalIgnoreCase))
            {
                string rest = u.Substring(gh.Length).Trim('/');
                string[] parts = rest.Split('/');
                if (parts.Length >= 2)
                {
                    string repo = parts[1];
                    if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo.Substring(0, repo.Length - 4);
                    return "https://raw.githubusercontent.com/" + parts[0] + "/" + repo + "/main/";
                }
            }
            if (!u.EndsWith("/")) u += "/";
            return u;
        }

        static void Log(string s)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppInfo.DataDir(), "update.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + s + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        static string FindCsc()
        {
            string win = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(win)) win = @"C:\Windows";
            string a = Path.Combine(win, @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
            if (File.Exists(a)) return a;
            string b = Path.Combine(win, @"Microsoft.NET\Framework\v4.0.30319\csc.exe");
            if (File.Exists(b)) return b;
            return null;
        }

        static string References(string csc)
        {
            string dir = Path.GetDirectoryName(csc);
            string win = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(win)) win = @"C:\Windows";
            StringBuilder sb = new StringBuilder("/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ");
            string[] names = { "System.Runtime.dll", "System.Runtime.WindowsRuntime.dll" };
            foreach (string n in names)
            {
                string p = Path.Combine(dir, n);
                if (File.Exists(p)) sb.Append("/r:\"" + p + "\" ");
            }
            // 최신 Windows 는 Windows.winmd 하나가 아니라 기능별 .winmd 여러 개로 나뉘어 있어요
            string wmDir = Path.Combine(win, @"System32\WinMetadata");
            if (Directory.Exists(wmDir))
            {
                foreach (string f in Directory.GetFiles(wmDir, "*.winmd"))
                    sb.Append("/r:\"" + f + "\" ");
            }
            return sb.ToString();
        }

        // 0 = up to date, 1 = installed (restart needed), -1 = temporary failure, -2 = this version failed
        public static int CheckAndApply(string baseUrl, int failedVersion, out int newVersion, out string msg)
        {
            newVersion = 0;
            msg = "";
            string b = NormalizeUrl(baseUrl);
            if (b.Length == 0) { msg = "업데이트 주소가 없어요"; return -1; }
            if (!b.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { msg = "주소는 https:// 로 시작해야 해요"; return -1; }

            Http v = Fetcher.Send(b + "version.txt");
            if (v.Error != null) { msg = "업데이트 서버에 연결하지 못했어요"; return -1; }
            if (v.Status != 200) { msg = "version.txt 를 찾지 못했어요 (" + v.Status + ")"; return -1; }
            string first = v.Body.Trim();
            int nl = first.IndexOfAny(new char[] { '\r', '\n' });
            if (nl > 0) first = first.Substring(0, nl).Trim();
            int ver;
            if (!int.TryParse(first, out ver)) { msg = "version.txt 형식이 올바르지 않아요"; return -1; }
            newVersion = ver;
            if (ver <= AppInfo.AppVersion) return 0;
            if (ver == failedVersion) { msg = "v" + ver + " 설치에 실패한 적이 있어 건너뛰어요"; return 0; }

            Http s = Fetcher.Send(b + "ScreenTranslator.cs");
            if (s.Error != null || s.Status != 200) { msg = "새 버전 파일을 받지 못했어요"; return -1; }
            string src = s.Body;
            if (src.Length < 5000 || src.Length > 3000000 ||
                src.IndexOf("namespace ScreenTranslatorApp", StringComparison.Ordinal) < 0)
            { msg = "받은 파일이 올바르지 않아요"; return -2; }
            Match m = Regex.Match(src, @"AppVersion\s*=\s*(\d+)\s*;");
            if (!m.Success || m.Groups[1].Value != ver.ToString())
            { msg = "받은 파일의 버전이 version.txt 와 달라요"; return -2; }

            string csc = FindCsc();
            if (csc == null) { msg = ".NET 컴파일러를 찾지 못했어요"; return -2; }
            string work = Path.Combine(AppInfo.DataDir(), "update");
            try { Directory.CreateDirectory(work); } catch { }
            string srcPath = Path.Combine(work, "ScreenTranslator.cs");
            string outPath = Path.Combine(work, "ScreenTranslator.new.exe");
            try
            {
                if (File.Exists(outPath)) File.Delete(outPath);
                File.WriteAllText(srcPath, src, new UTF8Encoding(true));
            }
            catch (Exception ex1) { msg = "임시 파일을 쓰지 못했어요"; Log(ex1.ToString()); return -1; }

            string ico = Path.Combine(AppInfo.AppDir(), "ScreenTranslator.ico");
            string args = "/nologo /target:winexe /optimize+ /codepage:65001 " +
                          (File.Exists(ico) ? "/win32icon:\"" + ico + "\" " : "") +
                          "/out:\"" + outPath + "\" " + References(csc) + "\"" + srcPath + "\"";
            string output = "";
            int code = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(csc, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(120000))
                    {
                        try { p.Kill(); } catch { }
                        msg = "빌드가 너무 오래 걸려서 멈췄어요";
                        return -1;
                    }
                    code = p.ExitCode;
                }
            }
            catch (Exception ex2) { msg = "빌드를 시작하지 못했어요"; Log(ex2.ToString()); return -1; }
            if (code != 0 || !File.Exists(outPath))
            {
                Log("build failed for v" + ver + ":\r\n" + output);
                msg = "새 버전 빌드에 실패했어요 (update.log 참고)";
                return -2;
            }

            string exe = Application.ExecutablePath;
            string oldPath = Path.Combine(AppInfo.AppDir(), "ScreenTranslator.old.exe");
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(exe, oldPath);
            }
            catch (Exception ex3) { Log(ex3.ToString()); msg = "실행 파일을 교체하지 못했어요"; return -1; }
            try
            {
                File.Move(outPath, exe);
            }
            catch (Exception ex4)
            {
                Log(ex4.ToString());
                try { File.Move(oldPath, exe); } catch { }
                msg = "새 실행 파일을 넣지 못했어요";
                return -1;
            }
            try { File.Copy(srcPath, Path.Combine(AppInfo.AppDir(), "ScreenTranslator.cs"), true); } catch { }
            Log("updated to v" + ver);
            msg = "v" + ver + " 설치 완료";
            return 1;
        }
    }

    // ------------------------------------------------------------------ main panel (버튼 UI)
    class MainPanel : Form
    {
        public Action RegionClicked, LiveClicked, FullClicked, ClearClicked, SettingsClicked, HiddenToTray;
        public bool ExcludedFromCapture;
        Button bLive;
        Label lbStatus;

        static readonly Color Bg = Color.FromArgb(24, 28, 34);
        static readonly Color BtnBg = Color.FromArgb(46, 53, 64);
        static readonly Color Accent = Color.FromArgb(255, 98, 52);
        static readonly Color Fg = Color.FromArgb(236, 240, 244);
        static readonly Color Dim = Color.FromArgb(150, 160, 172);

        public MainPanel()
        {
            Text = "화면 번역기";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Malgun Gothic", 9.5f);
            ClientSize = new Size(300, 392);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(wa.Left, wa.Right - Width - 24), wa.Top + 80);

            Label title = new Label();
            title.Text = "화면 번역기";
            title.Font = new Font("Malgun Gothic", 15f, FontStyle.Bold);
            title.ForeColor = Fg;
            title.SetBounds(18, 14, 264, 30);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "화면 속 외국어를 한국어로 바꿔 보여줘요";
            sub.ForeColor = Dim;
            sub.SetBounds(20, 46, 262, 20);
            Controls.Add(sub);

            Button bRegion = MakeButton("영역 선택해서 번역", 78, 46, Accent, 10.5f, delegate { if (RegionClicked != null) RegionClicked(); });
            bLive = MakeButton("영역 실시간 번역", 132, 46, BtnBg, 10.5f, delegate { if (LiveClicked != null) LiveClicked(); });
            Button bFull = MakeButton("화면 전체 번역", 186, 46, BtnBg, 10.5f, delegate { if (FullClicked != null) FullClicked(); });
            Button bClear = MakeButton("번역 지우기 / 중지", 240, 40, BtnBg, 9.5f, delegate { if (ClearClicked != null) ClearClicked(); });
            Button bSet = MakeButton("설정", 290, 36, BtnBg, 9.5f, delegate { if (SettingsClicked != null) SettingsClicked(); });

            ToolTip tip = new ToolTip();
            tip.SetToolTip(bRegion, "단축키: Ctrl+Alt+T");
            tip.SetToolTip(bLive, "단축키: Ctrl+Alt+L");
            tip.SetToolTip(bFull, "단축키: Ctrl+Alt+F");
            tip.SetToolTip(bClear, "단축키: Ctrl+Alt+X");

            lbStatus = new Label();
            lbStatus.Text = "준비됨";
            lbStatus.ForeColor = Dim;
            lbStatus.SetBounds(18, 336, 264, 34);
            Controls.Add(lbStatus);

            Label hint = new Label();
            hint.Text = "단축키  Ctrl+Alt+  T 영역 · L 실시간 · F 전체 · X 지우기";
            hint.Font = new Font("Malgun Gothic", 8f);
            hint.ForeColor = Dim;
            hint.SetBounds(18, 372, 270, 16);
            Controls.Add(hint);
        }

        Button MakeButton(string text, int y, int h, Color bg, float size, Action click)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(18, y, 264, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bg, 0.25f);
            b.BackColor = bg;
            b.ForeColor = Color.White;
            b.Font = new Font("Malgun Gothic", size, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            b.Click += delegate { click(); };
            Controls.Add(b);
            return b;
        }

        public void SetLive(bool on)
        {
            bLive.Text = on ? "실시간 번역 중지" : "영역 실시간 번역";
            Color c = on ? Color.FromArgb(200, 60, 40) : BtnBg;
            bLive.BackColor = c;
            bLive.FlatAppearance.MouseOverBackColor = ControlPaint.Light(c, 0.25f);
        }

        public void SetStatus(string s)
        {
            lbStatus.Text = s;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { ExcludedFromCapture = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); }
            catch { ExcludedFromCapture = false; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (HiddenToTray != null) HiddenToTray();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    // ------------------------------------------------------------------ settings window
    class SettingsForm : Form
    {
        ComboBox cbInterval;
        TextBox tbKey;
        CheckBox ckAuto, ckUpd;
        int[] intervals = { 1000, 1500, 3000, 5000 };

        public int IntervalMs { get { return intervals[Math.Max(0, cbInterval.SelectedIndex)]; } }
        public string DeepLKeyValue { get { return tbKey.Text.Trim(); } }
        public bool AutoStartValue { get { return ckAuto.Checked; } }
        public bool AutoUpdateValue { get { return ckUpd.Checked; } }

        public SettingsForm(Settings st, bool autoStart, Action checkUpdate, Action editUrl)
        {
            Text = "화면 번역기 설정";
            Font = new Font("Malgun Gothic", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 478);

            // --- 실시간 번역
            GroupBox g1 = new GroupBox();
            g1.Text = "실시간 번역";
            g1.SetBounds(14, 10, 432, 64);
            Controls.Add(g1);
            Label l1 = new Label();
            l1.Text = "화면 확인 주기";
            l1.SetBounds(14, 30, 110, 20);
            g1.Controls.Add(l1);
            cbInterval = new ComboBox();
            cbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            cbInterval.Items.AddRange(new object[] { "1초", "1.5초", "3초", "5초" });
            cbInterval.SetBounds(130, 26, 110, 24);
            int sel = 1, best = int.MaxValue;
            for (int i = 0; i < intervals.Length; i++)
            {
                int d = Math.Abs(intervals[i] - st.LiveIntervalMs);
                if (d < best) { best = d; sel = i; }
            }
            cbInterval.SelectedIndex = sel;
            g1.Controls.Add(cbInterval);
            Label l1b = new Label();
            l1b.Text = "화면이 바뀔 때만 번역해요";
            l1b.ForeColor = SystemColors.GrayText;
            l1b.SetBounds(250, 30, 170, 20);
            g1.Controls.Add(l1b);

            // --- 번역 엔진
            GroupBox g2 = new GroupBox();
            g2.Text = "번역 엔진";
            g2.SetBounds(14, 84, 432, 186);
            Controls.Add(g2);
            Label l2 = new Label();
            l2.Text = "기본은 무료 번역(Microsoft → Google → Lingva 순서로 자동 선택)이에요. 그래도 막히면 DeepL 무료 API 키를 넣어 주세요 (월 50만 자 무료, 키는 이 PC에만 저장). '번역 테스트'로 엔진별 상태를 볼 수 있어요.";
            l2.SetBounds(14, 22, 404, 52);
            g2.Controls.Add(l2);
            Label l2b = new Label();
            l2b.Text = "DeepL 키";
            l2b.SetBounds(14, 84, 70, 20);
            g2.Controls.Add(l2b);
            tbKey = new TextBox();
            tbKey.UseSystemPasswordChar = true;
            tbKey.Text = st.DeepLKey;
            tbKey.SetBounds(90, 80, 328, 24);
            g2.Controls.Add(tbKey);
            LinkLabel lk = new LinkLabel();
            lk.Text = "DeepL API 키 발급 페이지 열기";
            lk.SetBounds(14, 112, 250, 20);
            lk.LinkClicked += delegate
            {
                try { Process.Start("https://www.deepl.com/pro-api"); } catch { }
            };
            g2.Controls.Add(lk);
            Button bTest = new Button();
            bTest.Text = "번역 테스트";
            bTest.SetBounds(14, 142, 120, 30);
            bTest.Click += delegate
            {
                Translator.DeepLKey = tbKey.Text.Trim();
                Translator.ResetCooldown();
                Cursor = Cursors.WaitCursor;
                bool ok;
                string msg;
                try { msg = Translator.Test(out ok); }
                finally { Cursor = Cursors.Default; }
                MessageBox.Show(this, msg, ok ? "번역 테스트 성공" : "번역 테스트 실패",
                    MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            g2.Controls.Add(bTest);

            // --- 일반
            GroupBox g3 = new GroupBox();
            g3.Text = "일반";
            g3.SetBounds(14, 280, 432, 144);
            Controls.Add(g3);
            ckAuto = new CheckBox();
            ckAuto.Text = "Windows 시작 시 자동 실행";
            ckAuto.Checked = autoStart;
            ckAuto.SetBounds(14, 24, 300, 22);
            g3.Controls.Add(ckAuto);
            ckUpd = new CheckBox();
            ckUpd.Text = "자동 업데이트 (6시간마다 확인)";
            ckUpd.Checked = st.AutoUpdate;
            ckUpd.SetBounds(14, 48, 300, 22);
            g3.Controls.Add(ckUpd);
            Button bU1 = new Button();
            bU1.Text = "지금 업데이트 확인";
            bU1.SetBounds(14, 78, 140, 28);
            bU1.Click += delegate { checkUpdate(); };
            g3.Controls.Add(bU1);
            Button bU2 = new Button();
            bU2.Text = "업데이트 주소...";
            bU2.SetBounds(162, 78, 120, 28);
            bU2.Click += delegate { editUrl(); };
            g3.Controls.Add(bU2);
            Button bU3 = new Button();
            bU3.Text = "OCR 언어 보기";
            bU3.SetBounds(290, 78, 128, 28);
            bU3.Click += delegate
            {
                List<string> l = OcrService.Languages();
                MessageBox.Show(this, l.Count == 0 ? "설치된 OCR 언어가 없어요.\n설정 > 시간 및 언어 > 언어 및 지역에서 추가해 주세요." : string.Join(", ", l.ToArray()), "OCR 언어");
            };
            g3.Controls.Add(bU3);
            Label ver = new Label();
            ver.Text = "현재 버전: v" + AppInfo.AppVersion;
            ver.ForeColor = SystemColors.GrayText;
            ver.SetBounds(14, 116, 200, 20);
            g3.Controls.Add(ver);

            Button ok2 = new Button();
            ok2.Text = "저장";
            ok2.SetBounds(270, 436, 86, 30);
            ok2.DialogResult = DialogResult.OK;
            Controls.Add(ok2);
            Button cancel = new Button();
            cancel.Text = "취소";
            cancel.SetBounds(362, 436, 86, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            AcceptButton = ok2;
            CancelButton = cancel;
        }
    }

    // ------------------------------------------------------------------ tray app
    class TrayForm : Form
    {
        Settings st;
        NotifyIcon notify;
        ContextMenuStrip menu;
        ToolStripMenuItem miLive;
        MainPanel panel;
        System.Windows.Forms.Timer liveTimer, updTimer;

        OverlayForm overlay;
        bool liveOn, liveBusy, updBusy, oldCleaned, hintShown;
        Rectangle liveRect;
        byte[] lastSig;
        int jobId;
        DateTime startTime = DateTime.UtcNow;
        DateTime nextUpd = DateTime.UtcNow.AddSeconds(30);

        public TrayForm()
        {
            st = Settings.Load();
            Translator.DeepLKey = st.DeepLKey;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Size = new Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            IntPtr h = Handle; // 핫키용 핸들 미리 생성

            panel = new MainPanel();
            panel.RegionClicked = delegate { Post(delegate { StartRegion(false); }); };
            panel.LiveClicked = delegate { Post(delegate { if (liveOn) StopAll(); else StartRegion(true); }); };
            panel.FullClicked = delegate { Post(StartFull); };
            panel.ClearClicked = delegate { Post(StopAll); };
            panel.SettingsClicked = delegate { Post(ShowSettings); };
            panel.HiddenToTray = delegate
            {
                if (hintShown) return;
                hintShown = true;
                Notify("화면 번역기", "창을 닫아도 트레이에서 계속 실행 중이에요. 트레이 아이콘을 누르면 다시 열려요.");
            };

            BuildMenu();
            notify = new NotifyIcon();
            try { notify.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { notify.Icon = SystemIcons.Application; }
            notify.Text = "화면 번역기";
            notify.ContextMenuStrip = menu;
            notify.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Post(ShowPanel);
            };
            notify.Visible = true;

            liveTimer = new System.Windows.Forms.Timer();
            liveTimer.Interval = st.LiveIntervalMs;
            liveTimer.Tick += OnLiveTick;
            updTimer = new System.Windows.Forms.Timer();
            updTimer.Interval = 5000;
            updTimer.Tick += OnUpdTick;
            updTimer.Start();

            if (Program.JustUpdated)
                Notify("화면 번역기", "v" + AppInfo.AppVersion + " 로 업데이트됐어요");
            if (OcrService.Languages().Count == 0)
                Notify("OCR 언어팩 없음", "설정 > 시간 및 언어 > 언어 및 지역에서 번역할 언어를 추가해 주세요");

            if (!Program.Autostart)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 250;
                t.Tick += delegate { t.Stop(); t.Dispose(); ShowPanel(); };
                t.Start();
            }
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            uint m = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
            Native.RegisterHotKey(Handle, 1, m, (uint)Keys.T);
            Native.RegisterHotKey(Handle, 2, m, (uint)Keys.L);
            Native.RegisterHotKey(Handle, 3, m, (uint)Keys.F);
            Native.RegisterHotKey(Handle, 4, m, (uint)Keys.X);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            for (int i = 1; i <= 4; i++) Native.UnregisterHotKey(Handle, i);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                Post(delegate
                {
                    if (id == 1) StartRegion(false);
                    else if (id == 2) { if (liveOn) StopAll(); else StartRegion(true); }
                    else if (id == 3) StartFull();
                    else if (id == 4) StopAll();
                });
                return;
            }
            base.WndProc(ref m);
        }

        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        void Notify(string title, string text)
        {
            try { notify.ShowBalloonTip(4000, title, text, ToolTipIcon.Info); } catch { }
        }

        void ShowPanel()
        {
            panel.Show();
            if (panel.WindowState == FormWindowState.Minimized) panel.WindowState = FormWindowState.Normal;
            panel.Activate();
        }

        // ---------- menu ----------
        ToolStripMenuItem MI(string text, EventHandler h)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text);
            if (h != null) i.Click += h;
            return i;
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            ToolStripMenuItem open = MI("화면 번역기 열기", delegate { Post(ShowPanel); });
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MI("영역 선택해서 번역  (Ctrl+Alt+T)", delegate { Post(delegate { StartRegion(false); }); }));
            miLive = MI("영역 실시간 번역  (Ctrl+Alt+L)", delegate { Post(delegate { if (liveOn) StopAll(); else StartRegion(true); }); });
            menu.Items.Add(miLive);
            menu.Items.Add(MI("현재 화면 전체 번역  (Ctrl+Alt+F)", delegate { Post(StartFull); }));
            menu.Items.Add(MI("번역 지우기 / 실시간 중지  (Ctrl+Alt+X)", delegate { Post(StopAll); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MI("설정...", delegate { Post(ShowSettings); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MI("종료", delegate { Close(); }));
            menu.Opening += delegate { miLive.Checked = liveOn; };
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        bool GetAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue("ScreenTranslator") != null;
            }
            catch { return false; }
        }

        void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("ScreenTranslator", "\"" + Application.ExecutablePath + "\" --autostart");
                    else k.DeleteValue("ScreenTranslator", false);
                }
            }
            catch { }
        }

        void ShowSettings()
        {
            string oldKey = st.DeepLKey;
            using (SettingsForm f = new SettingsForm(st, GetAutoStart(),
                delegate { StartUpdateCheck(true); }, delegate { EditUpdateUrl(); }))
            {
                f.TopMost = true;
                if (f.ShowDialog() != DialogResult.OK)
                {
                    Translator.DeepLKey = oldKey; // 테스트하면서 바뀐 값 되돌리기
                    return;
                }
                bool keyChanged = f.DeepLKeyValue != oldKey;
                st.LiveIntervalMs = f.IntervalMs;
                liveTimer.Interval = st.LiveIntervalMs;
                st.DeepLKey = f.DeepLKeyValue;
                Translator.DeepLKey = st.DeepLKey;
                if (keyChanged) Translator.ResetCooldown();
                st.AutoUpdate = f.AutoUpdateValue;
                if (f.AutoStartValue != GetAutoStart()) SetAutoStart(f.AutoStartValue);
                st.Save();
                panel.SetStatus("설정을 저장했어요");
            }
        }

        // ---------- capture ----------
        static Bitmap CaptureScreen(Rectangle r)
        {
            Bitmap b = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            return b;
        }

        // 번역기 창 자체가 캡처에 찍히지 않게 해요
        Bitmap CaptureClean(Rectangle r)
        {
            bool tmp = panel != null && panel.Visible && !panel.ExcludedFromCapture;
            if (tmp) { panel.Opacity = 0; Application.DoEvents(); Thread.Sleep(80); }
            try { return CaptureScreen(r); }
            finally { if (tmp) panel.Opacity = 1; }
        }

        static byte[] Signature(Bitmap b)
        {
            using (Bitmap s = new Bitmap(24, 24))
            {
                using (Graphics g = Graphics.FromImage(s))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(b, 0, 0, 24, 24);
                }
                byte[] sig = new byte[24 * 24];
                for (int y = 0; y < 24; y++)
                    for (int x = 0; x < 24; x++)
                    {
                        Color c = s.GetPixel(x, y);
                        sig[y * 24 + x] = (byte)((c.R * 3 + c.G * 6 + c.B) / 10);
                    }
                return sig;
            }
        }

        static bool Changed(byte[] a, byte[] b)
        {
            if (a == null || b == null) return true;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
            return d > 500;
        }

        // ---------- translate flows ----------
        void StopAll()
        {
            liveOn = false;
            liveTimer.Stop();
            jobId++;
            CloseOverlay();
            panel.SetLive(false);
            panel.SetStatus("준비됨");
        }

        void CloseOverlay()
        {
            if (overlay != null)
            {
                OverlayForm o = overlay;
                overlay = null;
                try { o.Close(); o.Dispose(); } catch { }
            }
        }

        void StartRegion(bool live)
        {
            StopAll();
            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap shot = CaptureClean(vs);
            Rectangle rect;
            using (SelectForm sf = new SelectForm(shot, vs))
            {
                sf.ShowDialog();
                rect = sf.Result;
            }
            if (rect.IsEmpty) { shot.Dispose(); return; }
            if (live)
            {
                shot.Dispose();
                BeginLive(rect);
            }
            else
            {
                Bitmap crop = shot.Clone(new Rectangle(rect.X - vs.X, rect.Y - vs.Y, rect.Width, rect.Height), shot.PixelFormat);
                shot.Dispose();
                BeginOnce(rect, crop);
            }
        }

        void StartFull()
        {
            StopAll();
            Rectangle r = Screen.FromPoint(Cursor.Position).Bounds;
            Bitmap bmp = CaptureClean(r);
            BeginOnce(r, bmp);
        }

        void BeginOnce(Rectangle rect, Bitmap bmp)
        {
            overlay = new OverlayForm(rect, false);
            overlay.Dismissed += delegate { CloseOverlay(); panel.SetStatus("준비됨"); };
            overlay.SetContent(new List<Item>(), "번역 중...");
            overlay.Show();
            panel.SetStatus("번역 중...");
            RunJob(bmp, true, ++jobId);
        }

        void BeginLive(Rectangle rect)
        {
            liveRect = rect;
            liveOn = true;
            lastSig = null;
            OcrService.BestLang = null;
            overlay = new OverlayForm(rect, true);
            overlay.SetContent(new List<Item>(), "실시간 번역 시작");
            overlay.Show();
            panel.SetLive(true);
            panel.SetStatus("실시간 번역 중... (중지: 버튼 또는 Ctrl+Alt+X)");
            liveTimer.Interval = st.LiveIntervalMs;
            liveTimer.Start();
            OnLiveTick(null, EventArgs.Empty);
        }

        void OnLiveTick(object sender, EventArgs e)
        {
            if (!liveOn || liveBusy || overlay == null) return;
            int cd = Translator.CooldownSeconds();
            if (cd > 0 && Translator.DeepLKey.Length == 0)
            {
                string m = "번역 서버 제한 중... " + cd + "초 뒤 자동으로 다시 시도해요";
                overlay.SetStatus(m);
                panel.SetStatus(m);
                lastSig = null;
                return;
            }
            bool hidden = false;
            if (!overlay.ExcludedFromCapture) { overlay.Opacity = 0; hidden = true; Application.DoEvents(); Thread.Sleep(30); }
            Bitmap bmp;
            try { bmp = CaptureClean(liveRect); }
            finally { if (hidden && overlay != null) overlay.Opacity = 1; }
            byte[] sig = Signature(bmp);
            if (!Changed(lastSig, sig)) { bmp.Dispose(); return; }
            bool first = lastSig == null;
            lastSig = sig;
            liveBusy = true;
            RunJob(bmp, first, ++jobId);
        }

        void RunJob(Bitmap bmp, bool detect, int id)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Item> items = new List<Item>();
                string err = "";
                try { items = Pipeline.Process(bmp, detect, out err); }
                catch (Exception ex) { err = "오류: " + ex.Message; }
                finally { bmp.Dispose(); }
                Post(delegate
                {
                    liveBusy = false;
                    if (id != jobId || overlay == null) return;
                    if (err.Length > 0 && items.Count == 0 && liveOn)
                    {
                        // 실시간 중 실패: 이전 번역은 그대로 두고 상태만 알려요. 다음 주기에 다시 시도해요.
                        overlay.SetStatus(err);
                        panel.SetStatus(err);
                        lastSig = null;
                        return;
                    }
                    string status = "";
                    if (err.Length > 0) status = err;
                    else if (items.Count == 0 && !liveOn) status = "번역할 외국어 텍스트를 찾지 못했어요 (클릭하면 닫혀요)";
                    overlay.SetContent(items, status);

                    string ps;
                    if (err.Length > 0) ps = err;
                    else if (items.Count == 0) ps = "번역할 외국어를 찾지 못했어요";
                    else if (liveOn) ps = "실시간 번역 중... (" + items.Count + "개)";
                    else ps = "번역 완료 (" + items.Count + "개) - 번역 화면을 클릭하면 닫혀요";
                    panel.SetStatus(ps);
                });
            });
        }

        // ---------- updates ----------
        void OnUpdTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            if (st.AutoUpdate && st.UpdateUrl.Length > 0 && !updBusy && now >= nextUpd) StartUpdateCheck(false);
            if (!oldCleaned && (now - startTime).TotalSeconds > 10)
            {
                oldCleaned = true;
                try
                {
                    string op = Path.Combine(AppInfo.AppDir(), "ScreenTranslator.old.exe");
                    if (File.Exists(op)) File.Delete(op);
                }
                catch { }
            }
        }

        void StartUpdateCheck(bool manual)
        {
            if (updBusy) return;
            if (st.UpdateUrl.Length == 0)
            {
                if (manual) Notify("업데이트", "업데이트 주소가 아직 없어요. 설정 > 업데이트 주소에서 입력해 주세요");
                return;
            }
            updBusy = true;
            nextUpd = DateTime.UtcNow.AddHours(6);
            string url = st.UpdateUrl;
            int failed = st.FailedVersion;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int nv;
                string msg;
                int res = Updater.CheckAndApply(url, failed, out nv, out msg);
                Post(delegate { OnUpdateResult(res, nv, msg, manual); });
            });
        }

        void OnUpdateResult(int res, int nv, string msg, bool manual)
        {
            updBusy = false;
            if (res == 1) { RestartApp(); return; }
            if (res == -2)
            {
                st.FailedVersion = nv;
                st.Save();
                Notify("업데이트 실패", msg);
                return;
            }
            if (manual) Notify("업데이트", msg.Length > 0 ? msg : "최신 버전이에요 (v" + AppInfo.AppVersion + ")");
        }

        void RestartApp()
        {
            try
            {
                string exe = Application.ExecutablePath;
                string extra = Program.Autostart ? " --autostart" : "";
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & start \"\" \"" + exe + "\" --updated" + extra);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch { return; }
            Close();
        }

        void EditUpdateUrl()
        {
            string res = null;
            using (Form f = new Form())
            {
                f.Text = "업데이트 주소 설정";
                f.Font = new Font("Malgun Gothic", 9f);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.TopMost = true;
                f.ClientSize = new Size(500, 270);
                Label lb = new Label();
                lb.Left = 14; lb.Top = 12; lb.Width = 472; lb.Height = 150;
                lb.Text = "새 버전을 받아올 GitHub 저장소 주소를 입력하세요.\r\n" +
                          "예: https://github.com/내아이디/screen-translator\r\n\r\n" +
                          "• 이 주소의 프로그램 코드를 내려받아 이 PC에서 컴파일·실행하게 돼요.\r\n" +
                          "• 반드시 본인이 직접 관리하는 저장소 주소만 입력하세요.\r\n" +
                          "• 끄려면 설정에서 자동 업데이트 체크를 해제하세요.";
                TextBox tb = new TextBox();
                tb.Left = 14; tb.Top = 172; tb.Width = 472;
                tb.Text = st.UpdateUrl;
                Button ok = new Button();
                ok.Text = "저장"; ok.Left = 304; ok.Top = 222; ok.Width = 84; ok.DialogResult = DialogResult.OK;
                Button cancel = new Button();
                cancel.Text = "취소"; cancel.Left = 402; cancel.Top = 222; cancel.Width = 84; cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(lb); f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog() == DialogResult.OK) res = tb.Text.Trim();
            }
            if (res == null || res.Length == 0) return;
            string norm = Updater.NormalizeUrl(res);
            if (!norm.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("주소는 https:// 로 시작해야 해요.", "업데이트 주소");
                return;
            }
            st.UpdateUrl = norm;
            st.FailedVersion = 0;
            st.Save();
            StartUpdateCheck(true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopAll();
            if (notify != null) { notify.Visible = false; notify.Dispose(); }
            if (panel != null) { try { panel.Dispose(); } catch { } }
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        public static bool JustUpdated;
        public static bool Autostart;

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            foreach (string a in args)
            {
                if (a == "--updated") JustUpdated = true;
                if (a == "--autostart") Autostart = true;
            }
            bool created;
            Mutex mutex = new Mutex(true, "Local\\ScreenTranslatorOverlay", out created);
            if (!created && JustUpdated)
            {
                for (int i = 0; i < 20 && !created; i++)
                {
                    mutex.Close();
                    Thread.Sleep(500);
                    mutex = new Mutex(true, "Local\\ScreenTranslatorOverlay", out created);
                }
            }
            if (!created)
            {
                MessageBox.Show("이미 실행 중이에요.\n화면 오른쪽 아래 트레이(^) 아이콘을 눌러 보세요.", "화면 번역기");
                return;
            }
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); ServicePointManager.Expect100Continue = false; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayForm());
            GC.KeepAlive(mutex);
        }
    }
}
