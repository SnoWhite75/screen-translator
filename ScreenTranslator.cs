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
        public const uint WDA_NONE = 0;
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
        public bool AutoClear = true;

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
                            case "autoclear": s.AutoClear = v != "0"; break;
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
                l.Add("autoclear=" + (AutoClear ? "1" : "0"));
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

        // 번역 메모리: 번역한 문장을 디스크에 저장해서 다음 실행에도 다시 쓰고, OCR이 살짝 흔들린 같은 문장도 재사용해요
        const int MaxMemory = 30000;
        static Dictionary<string, string> memory = new Dictionary<string, string>();    // 정리한 원문 -> 번역 ("" = 번역 불필요)
        static Dictionary<string, string> squeezed = new Dictionary<string, string>();  // 원문 -> 비교용 축약형
        static Dictionary<string, string> bySqueezed = new Dictionary<string, string>(); // 축약형 -> 원문
        static Dictionary<string, List<string>> idxHead = new Dictionary<string, List<string>>();
        static Dictionary<string, List<string>> idxTail = new Dictionary<string, List<string>>();
        static Queue<string> memOrder = new Queue<string>();
        static bool memDirty = false;
        static DateTime lastSave = DateTime.UtcNow;
        static readonly Regex WsRx = new Regex(@"\s+", RegexOptions.Compiled);
        static readonly Regex UrlRx = new Regex(@"^(https?://|www\.)\S+$|^[\w.+\-]+@[\w\-]+\.[\w.\-]+$|^[A-Za-z]:\\\S+$", RegexOptions.Compiled);
        public static long SavedChars = 0;
        public static int MemoryHits = 0;

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
            int letters = 0;
            bool wide = false;
            foreach (char c in s)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if (c >= 0x3000) wide = true;
            }
            if (letters == 0) return false;
            if (letters < 2 && !wide) return false;      // "A", "x" 같은 한 글자
            if (UrlRx.IsMatch(s.Trim())) return false;  // 주소·이메일·경로는 번역하지 않아요
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

        // ---------- 번역 메모리 ----------
        static string Norm(string s)
        {
            return WsRx.Replace(s, " ").Trim();
        }

        // 대소문자·공백·기호 차이와 OCR 단골 오인식(I/l/1, O/0)을 무시한 비교용 문자열
        static string Squeeze(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c0 in s)
            {
                char c = (c0 == 'I') ? 'l' : char.ToLowerInvariant(c0);
                if (!char.IsLetterOrDigit(c)) continue;
                if (c == '1' || c == 'i') c = 'l';
                else if (c == '0') c = 'o';
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string DigitsOf(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s) if (c >= '0' && c <= '9') sb.Append(c);
            return sb.ToString();
        }

        static int Lev(string a, string b, int max)
        {
            if (Math.Abs(a.Length - b.Length) > max) return max + 1;
            int[] prev = new int[b.Length + 1];
            int[] cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                int rowMin = cur[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                    cur[j] = v;
                    if (v < rowMin) rowMin = v;
                }
                if (rowMin > max) return max + 1;
                int[] t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        static void IndexAdd(Dictionary<string, List<string>> idx, string k, string key)
        {
            List<string> l;
            if (!idx.TryGetValue(k, out l)) { l = new List<string>(); idx[k] = l; }
            l.Add(key);
            if (l.Count > 40) l.RemoveAt(0);
        }

        static void IndexRemove(Dictionary<string, List<string>> idx, string k, string key)
        {
            List<string> l;
            if (idx.TryGetValue(k, out l)) { l.Remove(key); if (l.Count == 0) idx.Remove(k); }
        }

        static bool Indexable(string sq)
        {
            return sq.Length >= 40 && sq.Length <= 600;
        }

        static void MemAdd(string key, string value, bool dirty)
        {
            lock (memory)
            {
                if (memory.ContainsKey(key))
                {
                    memory[key] = value;
                    if (dirty) memDirty = true;
                    return;
                }
                memory[key] = value;
                memOrder.Enqueue(key);
                string sq = Squeeze(key);
                squeezed[key] = sq;
                if (sq.Length >= 6) bySqueezed[sq] = key;
                if (Indexable(sq))
                {
                    IndexAdd(idxHead, sq.Substring(0, 5), key);
                    IndexAdd(idxTail, sq.Substring(sq.Length - 5), key);
                }
                while (memory.Count > MaxMemory && memOrder.Count > 0)
                {
                    string old = memOrder.Dequeue();
                    string osq;
                    if (squeezed.TryGetValue(old, out osq))
                    {
                        if (Indexable(osq))
                        {
                            IndexRemove(idxHead, osq.Substring(0, 5), old);
                            IndexRemove(idxTail, osq.Substring(osq.Length - 5), old);
                        }
                        string cur;
                        if (bySqueezed.TryGetValue(osq, out cur) && cur == old) bySqueezed.Remove(osq);
                        squeezed.Remove(old);
                    }
                    memory.Remove(old);
                }
                if (dirty) memDirty = true;
            }
        }

        // 같은 문장이거나, 공백·대소문자·OCR 오인식 수준의 차이만 있는 문장이 이미 번역돼 있으면 그 번역을 돌려줘요.
        // 숫자가 다르면(점수, 시간 등) 절대 재사용하지 않아요.
        static bool MemLookup(string key, out string value)
        {
            lock (memory)
            {
                if (memory.TryGetValue(key, out value)) return true;
                value = null;
                string sq = Squeeze(key);
                if (sq.Length < 6) return false;
                string digits = DigitsOf(key);
                string k0;
                if (bySqueezed.TryGetValue(sq, out k0) && DigitsOf(k0) == digits)
                {
                    value = memory[k0];
                    return true;
                }
                if (!Indexable(sq)) return false;
                // 긴 문단은 OCR이 한두 글자 틀려도(문장 길이의 3% 이내) 같은 문장으로 봐요
                int maxD = 1 + (sq.Length - 40) / 80;
                List<string> cand = new List<string>();
                List<string> l;
                if (idxHead.TryGetValue(sq.Substring(0, 5), out l)) cand.AddRange(l);
                if (idxTail.TryGetValue(sq.Substring(sq.Length - 5), out l)) cand.AddRange(l);
                int bestD = int.MaxValue;
                foreach (string k in cand)
                {
                    string ksq;
                    if (!squeezed.TryGetValue(k, out ksq)) continue;
                    if (DigitsOf(k) != digits) continue;
                    int d = Lev(sq, ksq, maxD);
                    if (d <= maxD && d < bestD) { bestD = d; value = memory[k]; }
                }
                return value != null;
            }
        }

        static string MemPath()
        {
            return Path.Combine(AppInfo.DataDir(), "memory.tsv");
        }

        static string Esc(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static string Unesc(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 't' ? '\t' : (n == 'n' ? '\n' : (n == 'r' ? '\r' : n)));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static void LoadMemory()
        {
            try
            {
                string p = MemPath();
                if (!File.Exists(p)) return;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    int t = line.IndexOf('\t');
                    if (t <= 0) continue;
                    string val = Unesc(line.Substring(t + 1));
                    if (val.Length == 0) continue; // 예전 버전이 실패를 빈 번역으로 저장한 것 → 다시 번역
                    MemAdd(Unesc(line.Substring(0, t)), val, false);
                }
            }
            catch { }
        }

        // force=false: 바뀐 게 있을 때 30초에 한 번만 저장 / force=true: 바로 저장 (종료할 때)
        public static void SaveMemory(bool force)
        {
            try
            {
                List<string> lines;
                lock (memory)
                {
                    if (!memDirty) return;
                    if (!force && (DateTime.UtcNow - lastSave).TotalSeconds < 30) return;
                    lines = new List<string>(memory.Count);
                    foreach (string k in memOrder)
                    {
                        string v;
                        if (memory.TryGetValue(k, out v) && v.Length > 0) lines.Add(Esc(k) + "\t" + Esc(v));
                    }
                    memDirty = false;
                    lastSave = DateTime.UtcNow;
                }
                string p = MemPath();
                string tmp = p + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray(), new UTF8Encoding(false));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch { }
        }

        public static int MemoryCount()
        {
            lock (memory) { return memory.Count; }
        }

        public static void ClearMemory()
        {
            lock (memory)
            {
                memory.Clear();
                squeezed.Clear();
                bySqueezed.Clear();
                idxHead.Clear();
                idxTail.Clear();
                memOrder.Clear();
                memDirty = false;
            }
            SavedChars = 0;
            MemoryHits = 0;
            try { if (File.Exists(MemPath())) File.Delete(MemPath()); } catch { }
        }

        // 번역이 필요 없거나 실패한 항목은 null. 실패 이유는 LastError 에 남아요.
        public static string[] ToKorean(List<string> texts)
        {
            string[] result = new string[texts.Count];
            Dictionary<string, List<int>> pending = new Dictionary<string, List<int>>();
            List<string> uniq = new List<string>();
            for (int i = 0; i < texts.Count; i++)
            {
                string key = Norm(texts[i]);
                string hit;
                if (MemLookup(key, out hit))
                {
                    result[i] = hit.Length == 0 ? null : hit;
                    SavedChars += key.Length;
                    MemoryHits++;
                    continue;
                }
                List<int> l;
                if (!pending.TryGetValue(key, out l)) { l = new List<int>(); pending[key] = l; uniq.Add(key); }
                l.Add(i);
            }
            if (uniq.Count == 0) { LastError = ""; return result; }

            lock (gate)
            {
                int pos = 0;
                while (pos < uniq.Count)
                {
                    List<string> part = new List<string>();
                    int chars = 0;
                    while (pos < uniq.Count)
                    {
                        int len = uniq[pos].Length + 1;
                        if (part.Count > 0 && (chars + len > 3500 || part.Count >= 40)) break;
                        part.Add(uniq[pos]);
                        chars += len;
                        pos++;
                    }
                    string[] outp = TranslateChunk(part);
                    if (outp == null) return result; // 실패: 지금까지 된 것만 돌려줘요 (이유는 LastError)
                    for (int k = 0; k < part.Count; k++)
                    {
                        if (outp[k] == null) continue; // 이 항목만 실패: 기억하지 않고 다음에 다시 시도해요
                        string o = outp[k];
                        if (string.Equals(o.Trim(), part[k].Trim(), StringComparison.OrdinalIgnoreCase)) o = "";
                        MemAdd(part[k], o, true);
                        foreach (int idx in pending[part[k]]) result[idx] = o.Length == 0 ? null : o;
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
        public RectangleF Rect;       // 원문이 있던 자리 (번역문은 이 안에만 그려요)
        public string Text;
        public int Lines = 1;         // 원문 줄 수
        public float LineH;           // 원문 한 줄의 높이 (= 글자 크기에 가까워요)
        public Color Bg, Fg;
        public float FontSize;        // 배치 결과: 글자 크기
        public float Scale = 1f;      // 배치 결과: 가로 압축 비율 (1 = 그대로)
        public List<string> Wrapped;  // 배치 결과: 줄바꿈된 문장들
    }

    // 화면 픽셀을 한 번에 읽어 두는 도우미 (GetPixel 을 반복 호출하면 느려요)
    class Pix
    {
        public int W, H;
        int[] data;

        public Pix(Bitmap b)
        {
            W = b.Width;
            H = b.Height;
            System.Drawing.Imaging.BitmapData bd = b.LockBits(new Rectangle(0, 0, W, H),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            data = new int[W * H];
            try { Marshal.Copy(bd.Scan0, data, 0, data.Length); }
            finally { b.UnlockBits(bd); }
        }

        public Color At(int x, int y)
        {
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x >= W) x = W - 1;
            if (y >= H) y = H - 1;
            int v = data[y * W + x];
            return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        // 글자 상자 바로 바깥 테두리에서 가장 흔한 색 = 배경색
        public Color Background(RectangleF r)
        {
            int x0 = (int)Math.Floor(r.Left) - 2, y0 = (int)Math.Floor(r.Top) - 2;
            int x1 = (int)Math.Ceiling(r.Right) + 2, y1 = (int)Math.Ceiling(r.Bottom) + 2;
            int stepX = Math.Max(1, (x1 - x0) / 40), stepY = Math.Max(1, (y1 - y0) / 20);
            List<Color> samples = new List<Color>();
            for (int x = x0; x <= x1; x += stepX) { samples.Add(At(x, y0)); samples.Add(At(x, y1)); }
            for (int y = y0; y <= y1; y += stepY) { samples.Add(At(x0, y)); samples.Add(At(x1, y)); }
            Dictionary<int, int> count = new Dictionary<int, int>();
            int bestKey = 0, bestCnt = -1;
            foreach (Color c in samples)
            {
                int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                int n;
                count.TryGetValue(key, out n);
                n++;
                count[key] = n;
                if (n > bestCnt) { bestCnt = n; bestKey = key; }
            }
            long sr = 0, sg = 0, sb = 0, m = 0;
            foreach (Color c in samples)
            {
                int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                if (key != bestKey) continue;
                sr += c.R; sg += c.G; sb += c.B; m++;
            }
            if (m == 0) return Color.White;
            return Color.FromArgb((int)(sr / m), (int)(sg / m), (int)(sb / m));
        }

        static double Lin(int v)
        {
            double c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Rel(Color c)
        {
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        static double Contrast(Color a, Color b)
        {
            double la = Rel(a), lb = Rel(b);
            double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        // 상자 안에서 배경과 가장 다른 색 = 원문 글자색. 대비가 부족하면 검정/흰색.
        public Color Foreground(RectangleF r, Color bg)
        {
            int stepX = Math.Max(1, (int)(r.Width / 60)), stepY = Math.Max(1, (int)(r.Height / 8));
            long best = -1;
            Color bc = bg;
            for (int y = (int)r.Top; y < (int)r.Bottom; y += stepY)
                for (int x = (int)r.Left; x < (int)r.Right; x += stepX)
                {
                    Color c = At(x, y);
                    long dr = c.R - bg.R, dg = c.G - bg.G, db = c.B - bg.B;
                    long d = dr * dr + dg * dg + db * db;
                    if (d > best) { best = d; bc = c; }
                }
            if (best >= 0 && Contrast(bc, bg) >= 3.0) return bc;
            double lum = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
            return lum > 140 ? Color.FromArgb(20, 20, 20) : Color.FromArgb(244, 244, 244);
        }
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

        static readonly Regex ZeroWidth = new Regex(@"[\u200b-\u200f\u2028\u2029\ufeff]", RegexOptions.Compiled);
        static readonly Regex MultiSpace = new Regex(@"\s{2,}", RegexOptions.Compiled);

        static string Clean(string s)
        {
            s = ZeroWidth.Replace(s, "");
            s = MultiSpace.Replace(s, " ");
            return s.Trim();
        }

        // System.Runtime.WindowsRuntime.dll 의 AsTask() 대신 직접 완료를 기다려요 (참조 충돌 회피)
        static T Await<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            ManualResetEvent done = new ManualResetEvent(false);
            op.Completed = delegate (Windows.Foundation.IAsyncOperation<T> o, Windows.Foundation.AsyncStatus s) { done.Set(); };
            if (!done.WaitOne(20000)) throw new TimeoutException("OCR 시간 초과");
            return op.GetResults();
        }

        static List<LineInfo> Run(byte[] raw, int bw, int bh, WMO.OcrEngine eng, float factor)
        {
            List<LineInfo> list = new List<LineInfo>();
            using (WSS.DataWriter dw = new WSS.DataWriter())
            {
                // 비트맵 픽셀(BGRA)을 그대로 WinRT SoftwareBitmap 으로 넘겨요 (PNG 변환 없음)
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
                        string t = Clean(CjkSpace.Replace(ln.Text, ""));
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

        // 설치된 언어 목록을 다시 읽게 해요 (언어팩을 새로 설치한 뒤)
        public static void ResetEngines()
        {
            lock (engines) { engines.Clear(); }
            BestLang = null;
        }

        // detect: try every installed OCR language (in parallel) and keep the one that reads the most text
        public static List<LineInfo> Recognize(Bitmap src, bool detect)
        {
            Error = "";
            List<string> langs = Languages();
            if (langs.Count == 0)
            {
                Error = "OCR 언어팩이 없어요. 패널의 'OCR 언어 설치' 버튼으로 설치할 수 있어요";
                return new List<LineInfo>();
            }
            int maxDim = 2000;
            try { maxDim = (int)WMO.OcrEngine.MaxImageDimension; } catch { }
            int big = Math.Max(src.Width, src.Height);
            // 작은 영역(자막 등)만 2배로 키워 읽고, 큰 화면은 원래 크기로 읽어요. Windows OCR 은 큰 글씨를 더 키우면 오히려 조각내요.
            float factor = Math.Min(big >= 1000 ? 1f : 2f, (float)maxDim / big);
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
                // 픽셀은 한 번만 읽어서 모든 언어가 같이 써요
                byte[] raw;
                int bw, bh;
                System.Drawing.Imaging.BitmapData bd = prep.LockBits(new Rectangle(0, 0, prep.Width, prep.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                raw = new byte[bd.Stride * bd.Height];
                bw = bd.Width;
                bh = bd.Height;
                try { Marshal.Copy(bd.Scan0, raw, 0, raw.Length); }
                finally { prep.UnlockBits(bd); }

                List<string> tryTags = new List<string>();
                if (!detect && BestLang != null && langs.Contains(BestLang)) tryTags.Add(BestLang);
                else { foreach (string t in langs) { if (tryTags.Count < 6) tryTags.Add(t); } }

                int n = tryTags.Count;
                List<LineInfo>[] results = new List<LineInfo>[n];
                string[] errs = new string[n];
                if (n == 1)
                {
                    RunTag(tryTags[0], raw, bw, bh, factor, results, errs, 0);
                }
                else
                {
                    Thread[] th = new Thread[n];
                    for (int i = 0; i < n; i++)
                    {
                        int idx = i;
                        th[i] = new Thread(delegate () { RunTag(tryTags[idx], raw, bw, bh, factor, results, errs, idx); });
                        th[i].IsBackground = true;
                        th[i].Start();
                    }
                    for (int i = 0; i < n; i++) th[i].Join();
                }

                List<LineInfo> best = new List<LineInfo>();
                int bestScore = -1;
                string bestTag = null;
                for (int i = 0; i < n; i++)
                {
                    if (errs[i] != null) Error = errs[i];
                    if (results[i] == null) continue;
                    int score = 0;
                    foreach (LineInfo li in results[i]) score += Score(li.Text);
                    if (score > bestScore) { bestScore = score; best = results[i]; bestTag = tryTags[i]; }
                }
                if (bestTag != null && bestScore > 0) BestLang = bestTag;
                return best;
            }
            finally
            {
                if (own) prep.Dispose();
            }
        }

        // 언어 고르기 점수. 한글은 0점이에요: 한국어는 번역 대상이 아니고, 한국어 OCR 이 일본어·중국어 화면을
        // 엉터리 한글로 많이 읽어서 점수를 따 가는 바람에 엉뚱한 언어가 뽑히던 문제가 있었어요.
        // 가나는 일본어 OCR 만 읽을 수 있어서 높게 쳐요.
        static int Score(string t)
        {
            int sc = 0;
            foreach (char c in t)
            {
                if (c >= 0x3041 && c <= 0x30FF && c != 0x30FB) sc += 4;          // 히라가나·가타카나 (・ 제외)
                else if (c >= 0x4E00 && c <= 0x9FFF) sc += 3;                    // 한자
                else if (c >= 0xAC00 && c <= 0xD7A3) sc += 0;                    // 한글
                else if (c >= 0x3131 && c <= 0x318E) sc -= 2;                    // 낱자모(ㅏ, ㅎ) = 거의 항상 잘못 읽은 것
                else if (char.IsLetter(c)) sc += 2;                              // 라틴·키릴 등
                else if (char.IsDigit(c)) sc += 1;
            }
            return sc;
        }

        static void RunTag(string tag, byte[] raw, int bw, int bh, float factor, List<LineInfo>[] results, string[] errs, int idx)
        {
            try
            {
                WMO.OcrEngine eng = Engine(tag);
                if (eng == null) return;
                results[idx] = Run(raw, bw, bh, eng, factor);
            }
            catch (Exception ex) { errs[idx] = "OCR 오류: " + ex.Message; }
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
            public double LastH, SumH, LastW, MaxW;
        }

        static bool EndsSentence(string s)
        {
            if (s.Length == 0) return false;
            char c = s[s.Length - 1];
            return ".!?:;。！？：".IndexOf(c) >= 0;
        }

        static readonly Regex NumberedItem = new Regex(@"^(\(?\d{1,3}[\.\)]|\(?[A-Za-z][\.\)])\s", RegexOptions.Compiled);

        // 글머리 기호(•, -, 1. 등)로 시작하는 줄은 새 문단이에요
        static bool IsBullet(string s)
        {
            if (s.Length == 0) return false;
            if ("•·▪■□◆◇●○-–—*>»›▶►".IndexOf(s[0]) >= 0 && (s.Length == 1 || s[1] == ' ')) return true;
            return NumberedItem.IsMatch(s);
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
                    join = !EndsSentence(cur.Text) && !IsBullet(li.Text) &&
                           gap < 0.6 * h && gap > -0.5 * h &&
                           Math.Abs(li.Rect.Left - cur.Rect.Left) < 2.0 * h && ratio < 1.35;
                    // 메뉴·버튼·목록처럼 짧은 줄은 이어진 문장이 아니에요 (앞줄이 충분히 길거나 다음 줄이 소문자로 시작할 때만 합쳐요)
                    if (join && !(char.IsLower(li.Text[0]) || cur.LastW >= 12.0 * cur.LastH)) join = false;
                    // 앞줄이 다음 줄보다 훨씬 짧으면 제목·라벨이거나 문단의 끝이에요
                    if (join && cur.LastW < 0.6 * li.Rect.Width) join = false;
                    if (join && cur.Lines >= 2 && cur.LastW < 0.5 * cur.MaxW) join = false;
                }
                if (join)
                {
                    string t = cur.Text;
                    bool cjk = li.Text.Length > 0 && li.Text[0] >= 0x3000;
                    // 줄 끝 하이픈으로 쪼개진 단어는 붙여요 (trans- lation → translation)
                    if (t.Length > 2 && t[t.Length - 1] == '-' && char.IsLetter(t[t.Length - 2]) && li.Text.Length > 0 && char.IsLower(li.Text[0]))
                        cur.Text = t.Substring(0, t.Length - 1) + li.Text;
                    else
                        cur.Text = t + (cjk ? "" : " ") + li.Text;
                    cur.Rect = RectangleF.Union(cur.Rect, li.Rect);
                    cur.Lines++;
                    cur.LastH = li.Rect.Height;
                    cur.SumH += li.Rect.Height;
                    cur.LastW = li.Rect.Width;
                    if (li.Rect.Width > cur.MaxW) cur.MaxW = li.Rect.Width;
                }
                else
                {
                    cur = new Para();
                    cur.Text = li.Text;
                    cur.Rect = li.Rect;
                    cur.LastH = li.Rect.Height;
                    cur.SumH = li.Rect.Height;
                    cur.LastW = li.Rect.Width;
                    cur.MaxW = li.Rect.Width;
                    res.Add(cur);
                }
            }
            return res;
        }

        static bool IsWide(char c)
        {
            return c >= 0x3000;
        }

        // 꾸민 글꼴(글자 사이가 벌어진 메뉴 등)은 OCR 이 한 단어를 'コ' 'ンティ' 'ニュー' 처럼 조각내요.
        // 같은 높이에서 바로 옆에 붙은 조각(4글자 이하)은 한 줄로 다시 이어요.
        static List<LineInfo> JoinFragments(List<LineInfo> src)
        {
            List<LineInfo> l = new List<LineInfo>(src);
            bool again = true;
            while (again)
            {
                again = false;
                for (int i = 0; i < l.Count && !again; i++)
                {
                    for (int j = 0; j < l.Count && !again; j++)
                    {
                        if (i == j) continue;
                        LineInfo a = l[i], b = l[j];
                        if (a.Text.Length > 4 && b.Text.Length > 4) continue;
                        if (b.Rect.Left < a.Rect.Left) continue;
                        float h = Math.Min(a.Rect.Height, b.Rect.Height);
                        float overlap = Math.Min(a.Rect.Bottom, b.Rect.Bottom) - Math.Max(a.Rect.Top, b.Rect.Top);
                        if (overlap < 0.6f * h) continue;
                        float gap = b.Rect.Left - a.Rect.Right;
                        if (gap < -0.3f * h || gap > 0.8f * Math.Max(a.Rect.Height, b.Rect.Height)) continue;
                        bool wide = IsWide(a.Text[a.Text.Length - 1]) || IsWide(b.Text[0]);
                        LineInfo m = new LineInfo();
                        m.Text = a.Text + (wide ? "" : " ") + b.Text;
                        m.Rect = RectangleF.Union(a.Rect, b.Rect);
                        l[i] = m;
                        l.RemoveAt(j);
                        again = true;
                    }
                }
            }
            return l;
        }

        const string OkPunct = " .,!?:;'\"()[]-–—…・ー、。「」『』！？：；（）〜~%/&+#*";

        // OCR 이 그림·로고·장식을 글자로 잘못 읽은 줄을 걸러요
        static bool LooksLikeJunk(string t)
        {
            int letters = 0, weird = 0, hangul = 0, cjk = 0, jamo = 0;
            foreach (char c in t)
            {
                if (c >= 0xAC00 && c <= 0xD7A3) { hangul++; letters++; }
                else if (c >= 0x3131 && c <= 0x318E) { jamo++; }
                else if (c >= 0x3041 && c <= 0x9FFF) { cjk++; letters++; }
                else if (char.IsLetter(c)) letters++;
                else if (char.IsDigit(c)) { }
                else if (OkPunct.IndexOf(c) < 0) weird++;
            }
            if (letters == 0) return true;
            if (jamo > 0) return true;                       // ㅏ ㅎ 같은 낱자모는 실제 화면 글자에서 거의 안 나와요
            if (hangul > 0 && cjk > 0) return true;           // 한글과 가나·한자가 한 줄에 섞임 = 잘못 읽음
            if (weird * 3 > t.Length) return true;            // 이상한 기호가 1/3 이상
            if (cjk == 0 && letters <= 2 && t.Length <= 4) return true; // "4f", "l1" 같은 짧은 조각
            return false;
        }

        public static List<Item> Process(Bitmap bmp, bool detect, out string error)
        {
            error = "";
            List<LineInfo> lines = OcrService.Recognize(bmp, detect);
            if (OcrService.Error.Length > 0 && lines.Count == 0) { error = OcrService.Error; return new List<Item>(); }
            List<LineInfo> good = new List<LineInfo>();
            foreach (LineInfo li in JoinFragments(lines))
            {
                if (LooksLikeJunk(li.Text)) continue;
                if (li.Rect.Height > 1.4f * li.Rect.Width) continue; // 세로로 긴 줄: 그림·장식을 잘못 읽은 경우가 대부분이고, 가로 번역문을 넣을 수도 없어요
                good.Add(li);
            }
            List<Para> paras = Merge(good);
            List<Para> todo = new List<Para>();
            foreach (Para p in paras) if (Translator.WorthTranslating(p.Text)) todo.Add(p);

            List<string> srcTexts = new List<string>();
            foreach (Para p in todo) srcTexts.Add(p.Text);
            string[] outText = todo.Count > 0 ? Translator.ToKorean(srcTexts) : new string[0];
            if (todo.Count > 0 && Translator.LastError.Length > 0) error = Translator.LastError;

            List<Item> items = new List<Item>();
            Pix pix = null;
            RectangleF bounds = new RectangleF(0, 0, bmp.Width, bmp.Height);
            for (int i = 0; i < todo.Count; i++)
            {
                if (string.IsNullOrEmpty(outText[i])) continue;
                RectangleF r = RectangleF.Intersect(todo[i].Rect, bounds);
                if (r.Width < 2 || r.Height < 2) continue;
                if (pix == null) pix = new Pix(bmp);
                Item it = new Item();
                it.Rect = r;
                it.Text = outText[i];
                it.Lines = todo[i].Lines;
                it.LineH = (float)(todo[i].SumH / todo[i].Lines);
                it.Bg = pix.Background(r);
                it.Fg = pix.Foreground(r, it.Bg);
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
    // 번역 화면은 항상 "클릭 통과"예요. 아래 화면을 그대로 클릭·스크롤·입력할 수 있어요.
    class OverlayForm : Form
    {
        List<Item> items = new List<Item>();
        string status = "";
        static readonly Color KeyColor = Color.FromArgb(1, 2, 3);

        public OverlayForm(Rectangle bounds)
        {
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

        // 클릭 통과(TRANSPARENT)를 창 생성 때부터 넣어 둬요. 창이 다시 만들어져도 풀리지 않아요.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
        }

        public void SetStatus(string st)
        {
            status = st;
            Invalidate();
        }

        public void SetContent(List<Item> list, string st)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                foreach (Item it in list) FitItem(g, it);
            }
            items = list;
            status = st;
            Invalidate();
        }

        // ---- 배치: 번역문이 원문 상자(가로·세로) 밖으로 절대 나가지 않게 해요 ----
        // 단어 폭은 100px 기준으로 한 번씩만 재고, 줄바꿈은 그 합으로 계산해요 (빠름).
        static List<string> Wrap(string[] words, float[] ww, float sp, float limit, Func<string, float> w)
        {
            List<string> lines = new List<string>();
            StringBuilder cur = new StringBuilder();
            float curW = 0f;
            for (int i = 0; i < words.Length; i++)
            {
                float wd = ww[i];
                if (cur.Length == 0)
                {
                    if (wd <= limit) { cur.Append(words[i]); curW = wd; continue; }
                }
                else if (curW + sp + wd <= limit)
                {
                    cur.Append(' ').Append(words[i]);
                    curW += sp + wd;
                    continue;
                }
                else
                {
                    lines.Add(cur.ToString());
                    cur.Length = 0;
                    curW = 0f;
                    if (wd <= limit) { cur.Append(words[i]); curW = wd; continue; }
                }
                // 한 줄보다 긴 단어는 글자 단위로 쪼개요
                string word = words[i];
                int start = 0;
                while (start < word.Length)
                {
                    int n = 1;
                    while (start + n < word.Length && w(word.Substring(start, n + 1)) <= limit) n++;
                    string piece = word.Substring(start, n);
                    start += n;
                    if (start < word.Length) lines.Add(piece);
                    else { cur.Append(piece); curW = w(piece); }
                }
            }
            if (cur.Length > 0) lines.Add(cur.ToString());
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        // 1) 글자를 가로로 최대 22%까지 눌러서 먼저 맞춰 보고  2) 그래도 안 되면 글자 크기를 줄여요.
        static void FitItem(Graphics g, Item it)
        {
            RectangleF box = it.Rect;
            float s0 = Math.Max(8f, Math.Min(it.LineH * 1.05f, 72f)); // 원문 글자 크기보다 커지지 않아요
            const float MinScale = 0.78f;
            string[] words = it.Text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                it.Wrapped = new List<string>();
                it.Wrapped.Add("");
                it.FontSize = s0;
                it.Scale = 1f;
                return;
            }
            using (Font refFont = new Font("Malgun Gothic", 100f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                Dictionary<string, float> cache = new Dictionary<string, float>();
                Func<string, float> w = delegate (string s)
                {
                    float v;
                    if (cache.TryGetValue(s, out v)) return v;
                    v = g.MeasureString(s, refFont, 1000000, sf).Width;
                    cache[s] = v;
                    return v;
                };
                float[] ww = new float[words.Length];
                for (int i = 0; i < words.Length; i++) ww[i] = w(words[i]);
                float sp = Math.Max(1f, w("a a") - w("aa"));

                Func<float, float, List<string>> wrapAt = delegate (float size, float c)
                {
                    return Wrap(words, ww, sp, box.Width / c * 100f / size, w);
                };
                Func<float, float, bool> fits = delegate (float size, float c)
                {
                    return wrapAt(size, c).Count * size * 1.1f <= box.Height + 0.5f;
                };

                float fs;
                if (fits(s0, MinScale)) fs = s0;
                else if (!fits(8f, MinScale)) fs = 8f;
                else
                {
                    float a = 8f, b = s0; // a: 들어감, b: 안 들어감
                    while (b - a > 0.5f)
                    {
                        float m = (a + b) / 2f;
                        if (fits(m, MinScale)) a = m; else b = m;
                    }
                    fs = a;
                }

                float sc = 1f;
                if (!fits(fs, 1f))
                {
                    float c0 = MinScale, c1 = 1f; // c0: 들어감, c1: 안 들어감
                    if (!fits(fs, c0)) sc = c0;
                    else
                    {
                        while (c1 - c0 > 0.02f)
                        {
                            float m = (c0 + c1) / 2f;
                            if (fits(fs, m)) c0 = m; else c1 = m;
                        }
                        sc = c0;
                    }
                }
                it.FontSize = fs;
                it.Scale = sc;
                it.Wrapped = wrapAt(fs, sc);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(KeyColor); // 키 색상 = 투명, 번역 박스만 불투명하게 그려요
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            RectangleF all = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                // GenericTypographic 에 기본으로 들어 있는 LineLimit 은 "줄 높이보다 낮은 칸에는 글자를 아예 안 그림" 이라서
                // 번역 박스만 칠해지고 글자가 사라졌어요. 플래그를 직접 지정하고, 칸이 아니라 좌표에 그려요.
                sf.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                sf.Trimming = StringTrimming.None;
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Near;
                foreach (Item it in items)
                {
                    if (it.Wrapped == null) continue;
                    // 원문 글자(내려쓴 획 포함)가 비치지 않도록 상하를 조금 더 덮되, 그 밖으로는 나가지 않아요
                    float padY = Math.Max(1f, Math.Min(4f, it.LineH * 0.12f));
                    RectangleF cover = RectangleF.Inflate(it.Rect, 2f, padY);
                    cover.Intersect(all);
                    if (cover.Width <= 0 || cover.Height <= 0) continue;
                    using (SolidBrush bg = new SolidBrush(it.Bg)) g.FillRectangle(bg, cover);
                    g.SetClip(cover);
                    float slot = it.Rect.Height / it.Wrapped.Count;
                    float sc = it.Scale <= 0f ? 1f : it.Scale;
                    using (Font f = new Font("Malgun Gothic", it.FontSize, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (SolidBrush fg = new SolidBrush(it.Fg))
                    {
                        float lh = f.GetHeight(g); // 글꼴 한 줄 높이
                        GraphicsState gs = g.Save();
                        g.TranslateTransform(it.Rect.Left, it.Rect.Top);
                        g.ScaleTransform(sc, 1f);
                        for (int i = 0; i < it.Wrapped.Count; i++)
                        {
                            float y = i * slot + (slot - lh) / 2f; // 칸 가운데에 맞춰요
                            g.DrawString(it.Wrapped[i], f, fg, new PointF(0f, y), sf);
                        }
                        g.Restore(gs);
                    }
                    g.ResetClip();
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

    // ------------------------------------------------------------------ OCR language packs
    class LangPack
    {
        public string Cap, Name;
        public string[] Keys;
        public LangPack(string cap, string name, params string[] keys)
        {
            Cap = cap;
            Name = name;
            Keys = keys;
        }
    }

    // 설치 안 된 OCR 언어팩을 사용자 승인 후에 설치해요 (Windows 기능 설치라 관리자 권한 확인 창이 떠요)
    static class LangInstaller
    {
        static readonly LangPack[] Packs = new LangPack[]
        {
            new LangPack("en-US", "영어", "en"),
            new LangPack("ja-JP", "일본어", "ja"),
            new LangPack("zh-CN", "중국어 (간체)", "zh-hans", "zh-cn"),
            new LangPack("zh-TW", "중국어 (번체)", "zh-hant", "zh-tw", "zh-hk"),
            new LangPack("ko-KR", "한국어", "ko"),
            new LangPack("ru-RU", "러시아어", "ru"),
            new LangPack("de-DE", "독일어", "de"),
            new LangPack("fr-FR", "프랑스어", "fr"),
            new LangPack("es-ES", "스페인어", "es"),
            new LangPack("it-IT", "이탈리아어", "it"),
            new LangPack("pt-BR", "포르투갈어", "pt"),
            new LangPack("nl-NL", "네덜란드어", "nl"),
            new LangPack("pl-PL", "폴란드어", "pl"),
            new LangPack("tr-TR", "터키어", "tr"),
            new LangPack("uk-UA", "우크라이나어", "uk"),
            new LangPack("ar-SA", "아랍어", "ar"),
            new LangPack("th-TH", "태국어", "th"),
            new LangPack("vi-VN", "베트남어", "vi")
        };

        static bool running;

        static bool Match(string tag, string key)
        {
            string t = tag.ToLowerInvariant();
            return t == key || t.StartsWith(key + "-", StringComparison.Ordinal);
        }

        static bool IsInstalled(LangPack p, List<string> have)
        {
            foreach (string h in have)
                foreach (string k in p.Keys)
                    if (Match(h, k)) return true;
            return false;
        }

        static string BuildScript(List<LangPack> sel, string log)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("$ErrorActionPreference='Continue';");
            sb.Append("$log='" + log.Replace("'", "''") + "';$r=@();");
            sb.Append("try{$all=Get-WindowsCapability -Online}catch{$all=@();$r+=('ERR '+$_.Exception.Message)};");
            sb.Append("foreach($t in @(");
            for (int i = 0; i < sel.Count; i++) sb.Append((i > 0 ? "," : "") + "'" + sel[i].Cap + "'");
            sb.Append(")){");
            sb.Append("try{$c=$all | Where-Object { $_.Name -like ('Language.OCR~~~'+$t+'*') } | Select-Object -First 1;");
            sb.Append("if($c -eq $null){$r+=('NA '+$t)}");
            sb.Append("elseif($c.State -eq 'Installed'){$r+=('OK '+$t)}");
            sb.Append("else{Add-WindowsCapability -Online -Name $c.Name | Out-Null;$d=Get-WindowsCapability -Online -Name $c.Name;");
            sb.Append("if($d.State -eq 'Installed'){$r+=('OK '+$t)}else{$r+=('FAIL '+$t)}}");
            sb.Append("}catch{$r+=('ERR '+$t+' '+$_.Exception.Message)}};");
            sb.Append("$r | Out-File -Encoding utf8 $log");
            return sb.ToString();
        }

        static string NameOf(string cap)
        {
            foreach (LangPack p in Packs) if (p.Cap == cap) return p.Name;
            return cap;
        }

        static string Summarize(string result, out bool anyOk)
        {
            anyOk = false;
            if (result == null || result.Trim().Length == 0)
                return "설치 결과를 확인하지 못했어요.\n관리자 권한 확인 창에서 '아니요'를 눌렀거나, Windows 가 설치를 끝내지 못했을 수 있어요.";
            StringBuilder sb = new StringBuilder();
            foreach (string raw in result.Split('\n'))
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                string kind = sp > 0 ? line.Substring(0, sp) : line;
                string rest = sp > 0 ? line.Substring(sp + 1).Trim() : "";
                if (kind == "OK") { anyOk = true; sb.AppendLine("✔ " + NameOf(rest) + " - 설치됨"); }
                else if (kind == "NA") sb.AppendLine("✖ " + NameOf(rest) + " - 이 Windows 에서는 제공되지 않아요");
                else if (kind == "FAIL") sb.AppendLine("✖ " + NameOf(rest) + " - 설치 실패 (인터넷 연결 확인)");
                else sb.AppendLine("✖ " + line);
            }
            if (anyOk) sb.AppendLine("\n새 언어가 바로 안 보이면 프로그램을 한 번 껐다 켜 주세요.");
            return sb.ToString();
        }

        static void Finish(Form f, string result)
        {
            bool ok;
            string msg = Summarize(result, out ok);
            running = false;
            if (ok) OcrService.ResetEngines();
            bool alive = f != null && !f.IsDisposed;
            if (alive) MessageBox.Show(f, msg, "OCR 언어 설치", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            else MessageBox.Show(msg, "OCR 언어 설치", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (alive) f.Close();
        }

        static void StartInstall(List<LangPack> sel, Form f, Label status, Button btn)
        {
            string log = Path.Combine(Path.GetTempPath(), "st_ocr_install.log");
            try { if (File.Exists(log)) File.Delete(log); } catch { }
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(sel, log)));
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + b64);
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process proc;
            try { proc = Process.Start(psi); }
            catch (Exception ex)
            {
                btn.Enabled = true;
                status.Text = "";
                MessageBox.Show(f, "관리자 권한을 허용하지 않았거나 설치를 시작하지 못했어요.\n(" + ex.Message + ")", "OCR 언어 설치");
                return;
            }
            running = true;
            btn.Enabled = false;
            status.Text = "설치 중... 관리자 권한 확인 창이 뜨면 '예'를 눌러 주세요. 몇 분 걸릴 수 있어요.";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { if (proc != null) proc.WaitForExit(30 * 60 * 1000); } catch { }
                string result = "";
                try { if (File.Exists(log)) result = File.ReadAllText(log); } catch { }
                bool posted = false;
                try
                {
                    if (f != null && !f.IsDisposed)
                    {
                        f.BeginInvoke((Action)delegate { Finish(f, result); });
                        posted = true;
                    }
                }
                catch { }
                if (!posted) Finish(null, result);
            });
        }

        public static void Show(IWin32Window owner)
        {
            if (running)
            {
                MessageBox.Show("지금 설치 중이에요. 끝날 때까지 잠시만 기다려 주세요.", "OCR 언어 설치");
                return;
            }
            List<string> have = OcrService.Languages();
            List<LangPack> shown = new List<LangPack>();
            using (Form f = new Form())
            {
                f.Text = "OCR 언어 설치";
                f.Font = new Font("Malgun Gothic", 9f);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.TopMost = true;
                f.ClientSize = new Size(440, 470);

                Label info = new Label();
                info.Text = "글자를 읽으려면(OCR) 그 언어팩이 Windows 에 있어야 해요.\n읽을 언어를 체크하고 설치를 누르세요. 설치 전에 한 번 더 물어봐요.";
                info.SetBounds(14, 10, 412, 46);
                f.Controls.Add(info);

                CheckedListBox clb = new CheckedListBox();
                clb.CheckOnClick = true;
                clb.IntegralHeight = false;
                clb.SetBounds(14, 58, 412, 300);
                foreach (LangPack p in Packs)
                {
                    bool inst = IsInstalled(p, have);
                    shown.Add(p);
                    clb.Items.Add(p.Name + "  (" + p.Cap + ")" + (inst ? "   - 설치됨" : ""));
                }
                clb.ItemCheck += delegate (object s, ItemCheckEventArgs e)
                {
                    if (e.NewValue == CheckState.Checked && IsInstalled(shown[e.Index], have)) e.NewValue = CheckState.Unchecked;
                };
                f.Controls.Add(clb);

                Label status = new Label();
                status.ForeColor = SystemColors.GrayText;
                status.SetBounds(14, 366, 412, 40);
                f.Controls.Add(status);

                Button go = new Button();
                go.Text = "선택한 언어 설치";
                go.SetBounds(14, 420, 150, 32);
                go.Click += delegate
                {
                    List<LangPack> sel = new List<LangPack>();
                    for (int i = 0; i < clb.Items.Count; i++)
                        if (clb.GetItemChecked(i)) sel.Add(shown[i]);
                    if (sel.Count == 0)
                    {
                        MessageBox.Show(f, "설치할 언어를 체크해 주세요.", "OCR 언어 설치");
                        return;
                    }
                    StringBuilder names = new StringBuilder();
                    foreach (LangPack p in sel) names.AppendLine("  • " + p.Name);
                    string q = "다음 OCR 언어팩을 설치할까요?\n\n" + names +
                               "\n• Windows 가 인터넷에서 내려받아 설치해요 (언어당 수 MB).\n" +
                               "• 곧 '관리자 권한 확인' 창이 떠요. 허용해야 설치돼요.";
                    if (MessageBox.Show(f, q, "OCR 언어 설치", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    StartInstall(sel, f, status, go);
                };
                f.Controls.Add(go);

                Button close = new Button();
                close.Text = "닫기";
                close.SetBounds(340, 420, 86, 32);
                close.DialogResult = DialogResult.Cancel;
                f.Controls.Add(close);
                f.CancelButton = close;

                if (owner != null) f.ShowDialog(owner); else f.ShowDialog();
            }
        }
    }

    // ------------------------------------------------------------------ app info / self update
    static class AppInfo
    {
        public const int AppVersion = 10;

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
                string firstErr = "";
                foreach (string ol in output.Split('\n'))
                    if (ol.IndexOf("error CS", StringComparison.Ordinal) >= 0) { firstErr = ol.Trim(); break; }
                if (firstErr.Length > 150) firstErr = firstErr.Substring(firstErr.Length - 150);
                msg = "새 버전(v" + ver + ") 빌드에 실패했어요. " + firstErr;
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
        public Action RegionClicked, LiveClicked, FullClicked, ClearClicked, SettingsClicked, LangClicked, HiddenToTray;
        Button bLive;
        Label lbStatus;

        static readonly Color Bg = Color.FromArgb(24, 28, 34);
        static readonly Color BtnBg = Color.FromArgb(46, 53, 64);
        static readonly Color Accent = Color.FromArgb(255, 98, 52);
        static readonly Color Fg = Color.FromArgb(236, 240, 244);
        static readonly Color Dim = Color.FromArgb(150, 160, 172);

        public MainPanel()
        {
            Text = "화면 번역기 v" + AppInfo.AppVersion;
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
            title.Text = "화면 번역기  v" + AppInfo.AppVersion;
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
            Button bSet = MakeButtonAt("설정", 18, 290, 128, 36, BtnBg, 9.5f, delegate { if (SettingsClicked != null) SettingsClicked(); });
            Button bLang = MakeButtonAt("OCR 언어 설치", 154, 290, 128, 36, BtnBg, 9.5f, delegate { if (LangClicked != null) LangClicked(); });

            ToolTip tip = new ToolTip();
            tip.SetToolTip(bRegion, "단축키: Ctrl+Alt+T");
            tip.SetToolTip(bLive, "단축키: Ctrl+Alt+L");
            tip.SetToolTip(bFull, "단축키: Ctrl+Alt+F");
            tip.SetToolTip(bClear, "단축키: Ctrl+Alt+X");
            tip.SetToolTip(bLang, "읽을 수 없는 언어의 OCR 언어팩을 설치해요 (설치 전에 확인해요)");

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
            return MakeButtonAt(text, 18, y, 264, h, bg, size, click);
        }

        Button MakeButtonAt(string text, int x, int y, int w, int h, Color bg, float size, Action click)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
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
        CheckBox ckAuto, ckUpd, ckClear;
        Label lbMem;
        int[] intervals = { 1000, 1500, 3000, 5000 };

        public int IntervalMs { get { return intervals[Math.Max(0, cbInterval.SelectedIndex)]; } }
        public string DeepLKeyValue { get { return tbKey.Text.Trim(); } }
        public bool AutoStartValue { get { return ckAuto.Checked; } }
        public bool AutoUpdateValue { get { return ckUpd.Checked; } }
        public bool AutoClearValue { get { return ckClear.Checked; } }

        void RefreshMem()
        {
            lbMem.Text = "저장된 번역 " + Translator.MemoryCount() + "개  ·  이번 실행에서 다시 쓴 번역 " + Translator.MemoryHits +
                         "번 (글자 약 " + Translator.SavedChars + "자 절약)";
        }

        public SettingsForm(Settings st, bool autoStart, Action checkUpdate, Action editUrl)
        {
            Text = "화면 번역기 설정";
            Font = new Font("Malgun Gothic", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 594);

            // --- 번역 동작
            GroupBox g1 = new GroupBox();
            g1.Text = "번역 동작";
            g1.SetBounds(14, 10, 432, 92);
            Controls.Add(g1);
            Label l1 = new Label();
            l1.Text = "실시간 화면 확인 주기";
            l1.SetBounds(14, 30, 130, 20);
            g1.Controls.Add(l1);
            cbInterval = new ComboBox();
            cbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            cbInterval.Items.AddRange(new object[] { "1초", "1.5초", "3초", "5초" });
            cbInterval.SetBounds(150, 26, 100, 24);
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
            l1b.SetBounds(258, 30, 160, 20);
            g1.Controls.Add(l1b);
            ckClear = new CheckBox();
            ckClear.Text = "한 번 번역한 화면이 바뀌면(스크롤 등) 번역을 자동으로 지우기";
            ckClear.Checked = st.AutoClear;
            ckClear.SetBounds(14, 60, 408, 22);
            g1.Controls.Add(ckClear);

            // --- 번역 엔진
            GroupBox g2 = new GroupBox();
            g2.Text = "번역 엔진";
            g2.SetBounds(14, 112, 432, 186);
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

            // --- 번역 메모리
            GroupBox gm = new GroupBox();
            gm.Text = "번역 메모리 (한 번 번역한 문장은 다시 요청하지 않아요)";
            gm.SetBounds(14, 308, 432, 78);
            Controls.Add(gm);
            lbMem = new Label();
            lbMem.SetBounds(14, 22, 404, 20);
            gm.Controls.Add(lbMem);
            Button bMem1 = new Button();
            bMem1.Text = "메모리 폴더 열기";
            bMem1.SetBounds(14, 44, 140, 26);
            bMem1.Click += delegate
            {
                try { Process.Start(AppInfo.DataDir()); } catch { }
            };
            gm.Controls.Add(bMem1);
            Button bMem2 = new Button();
            bMem2.Text = "메모리 지우기";
            bMem2.SetBounds(162, 44, 120, 26);
            bMem2.Click += delegate
            {
                if (MessageBox.Show(this, "저장된 번역을 모두 지울까요?\n(지우면 같은 문장도 다시 번역 요청을 보내요)", "번역 메모리",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Translator.ClearMemory();
                RefreshMem();
            };
            gm.Controls.Add(bMem2);
            RefreshMem();

            // --- 일반
            GroupBox g3 = new GroupBox();
            g3.Text = "일반";
            g3.SetBounds(14, 396, 432, 144);
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
            bU3.Text = "OCR 언어 설치...";
            bU3.SetBounds(290, 78, 128, 28);
            bU3.Click += delegate { LangInstaller.Show(this); };
            g3.Controls.Add(bU3);
            Label ver = new Label();
            ver.Text = "현재 버전: v" + AppInfo.AppVersion;
            ver.ForeColor = SystemColors.GrayText;
            ver.SetBounds(14, 116, 200, 20);
            g3.Controls.Add(ver);

            Button ok2 = new Button();
            ok2.Text = "저장";
            ok2.SetBounds(270, 552, 86, 30);
            ok2.DialogResult = DialogResult.OK;
            Controls.Add(ok2);
            Button cancel = new Button();
            cancel.Text = "취소";
            cancel.SetBounds(362, 552, 86, 30);
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
        Action balloonAct;
        DateTime balloonAt = DateTime.MinValue, langHintAt = DateTime.MinValue;
        NotifyIcon notify;
        ContextMenuStrip menu;
        ToolStripMenuItem miLive;
        MainPanel panel;
        System.Windows.Forms.Timer liveTimer, updTimer;

        OverlayForm overlay;
        bool liveOn, liveBusy, updBusy, oldCleaned, hintShown;
        Rectangle liveRect, watchRect;
        byte[] lastSig, baseSig;
        System.Windows.Forms.Timer watchTimer;
        int jobId;
        DateTime startTime = DateTime.UtcNow;
        DateTime nextUpd = DateTime.UtcNow.AddSeconds(30);

        public TrayForm()
        {
            st = Settings.Load();
            Translator.DeepLKey = st.DeepLKey;
            Translator.LoadMemory();
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
            panel.LangClicked = delegate { Post(ShowLangInstaller); };
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
            notify.BalloonTipClicked += delegate
            {
                Action a = balloonAct;
                balloonAct = null;
                if (a != null && (DateTime.UtcNow - balloonAt).TotalSeconds < 60) Post(a);
            };
            notify.Visible = true;

            liveTimer = new System.Windows.Forms.Timer();
            liveTimer.Interval = st.LiveIntervalMs;
            liveTimer.Tick += OnLiveTick;
            updTimer = new System.Windows.Forms.Timer();
            updTimer.Interval = 5000;
            updTimer.Tick += OnUpdTick;
            updTimer.Start();
            watchTimer = new System.Windows.Forms.Timer();
            watchTimer.Interval = 900;
            watchTimer.Tick += OnWatchTick;

            if (Program.JustUpdated)
                Notify("화면 번역기", "v" + AppInfo.AppVersion + " 로 업데이트됐어요");
            if (OcrService.Languages().Count == 0)
                NotifyAct("OCR 언어팩이 없어요", "여기를 누르면 OCR 언어팩을 설치할 수 있어요", ShowLangInstaller);

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

        void NotifyAct(string title, string text, Action act)
        {
            balloonAct = act;
            balloonAt = DateTime.UtcNow;
            Notify(title, text);
        }

        void ShowLangInstaller()
        {
            LangInstaller.Show(null);
            lastSig = null;
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
            menu.Items.Add(MI("OCR 언어 설치...", delegate { Post(ShowLangInstaller); }));
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
                st.AutoClear = f.AutoClearValue;
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
        // 번역 화면·버튼 창은 평소 녹화·스크린샷에 그대로 찍혀요.
        // 번역기가 화면을 읽는 그 순간(약 0.06초)에만 캡처에서 빼서, 자기 번역문을 다시 읽지 않게 해요.
        // (화면에서는 깜빡이지 않아요. 아주 오래된 Windows 에서만 잠깐 숨겼다가 다시 보여줘요.)
        Bitmap CaptureClean(Rectangle r)
        {
            List<Form> toggled = new List<Form>();
            List<Form> hidden = new List<Form>();
            Form[] wins = new Form[] { overlay, panel };
            foreach (Form f in wins)
            {
                if (f == null || f.IsDisposed || !f.Visible) continue;
                bool ok = false;
                try { ok = Native.SetWindowDisplayAffinity(f.Handle, Native.WDA_EXCLUDEFROMCAPTURE); } catch { ok = false; }
                if (ok) toggled.Add(f);
                else { f.Opacity = 0; hidden.Add(f); }
            }
            if (hidden.Count > 0) Application.DoEvents();
            if (toggled.Count + hidden.Count > 0) Thread.Sleep(60);
            try { return CaptureScreen(r); }
            finally
            {
                foreach (Form f in toggled)
                {
                    try { if (!f.IsDisposed) Native.SetWindowDisplayAffinity(f.Handle, Native.WDA_NONE); } catch { }
                }
                foreach (Form f in hidden)
                {
                    try { if (!f.IsDisposed) f.Opacity = 1; } catch { }
                }
            }
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
            return Changed(a, b, 500);
        }

        static bool Changed(byte[] a, byte[] b, int limit)
        {
            if (a == null || b == null) return true;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
            return d > limit;
        }

        // 한 번 번역한 화면이 스크롤 등으로 바뀌면 낡은 번역을 자동으로 지워요
        void OnWatchTick(object sender, EventArgs e)
        {
            if (overlay == null || liveOn || baseSig == null) { watchTimer.Stop(); return; }
            Bitmap bmp = CaptureClean(watchRect);
            byte[] sig = Signature(bmp);
            bmp.Dispose();
            if (Changed(baseSig, sig, 1800))
            {
                watchTimer.Stop();
                CloseOverlay();
                panel.SetStatus("화면이 바뀌어서 번역을 지웠어요");
            }
        }

        // ---------- translate flows ----------
        void StopAll()
        {
            liveOn = false;
            liveTimer.Stop();
            watchTimer.Stop();
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
            watchTimer.Stop();
            watchRect = rect;
            baseSig = Signature(bmp);
            overlay = new OverlayForm(rect);
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
            overlay = new OverlayForm(rect);
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
            Bitmap bmp = CaptureClean(liveRect);
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
                    else if (items.Count == 0 && !liveOn) status = "번역할 외국어 텍스트를 찾지 못했어요";
                    overlay.SetContent(items, status);

                    string ps;
                    if (err.Length > 0) ps = err;
                    else if (items.Count == 0) ps = "번역할 외국어를 찾지 못했어요";
                    else if (liveOn) ps = "실시간 번역 중... (" + items.Count + "개)";
                    else ps = st.AutoClear ? "번역 완료 (" + items.Count + "개) - 화면이 바뀌면 자동으로 지워져요"
                                           : "번역 완료 (" + items.Count + "개) - 지우기: Ctrl+Alt+X";
                    panel.SetStatus(ps);

                    if (!liveOn)
                    {
                        if (items.Count == 0)
                        {
                            // 번역할 게 없으면 안내만 잠깐 보여주고 닫아요
                            System.Windows.Forms.Timer bye = new System.Windows.Forms.Timer();
                            bye.Interval = err.Length > 0 ? 6000 : 2500;
                            bye.Tick += delegate { bye.Stop(); bye.Dispose(); if (id == jobId) CloseOverlay(); };
                            bye.Start();
                            // 글자를 못 읽었을 때: 다른 언어라면 OCR 언어팩이 없어서일 수 있어요 (5분에 한 번만 알려줘요)
                            if (err.Length == 0 && (DateTime.UtcNow - langHintAt).TotalMinutes > 5)
                            {
                                langHintAt = DateTime.UtcNow;
                                NotifyAct("번역할 글자를 찾지 못했어요", "다른 언어의 글자라면 OCR 언어팩이 없어서일 수 있어요. 여기를 누르면 설치할 수 있어요", ShowLangInstaller);
                            }
                        }
                        else if (st.AutoClear) watchTimer.Start();
                    }
                });
            });
        }

        // ---------- updates ----------
        void OnUpdTick(object sender, EventArgs e)
        {
            Translator.SaveMemory(false);
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
            Translator.SaveMemory(true);
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
