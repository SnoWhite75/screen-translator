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
            Http res = new Http();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.UserAgent = "Mozilla/5.0 ScreenTranslator/1.0";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse()) Fill(res, resp);
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null) { using (resp) { Fill(res, resp); } }
                else res.Error = ex.Message;
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
    static class Translator
    {
        static Dictionary<string, string> cache = new Dictionary<string, string>();
        public static string LastError = "";

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

        // returns null if no translation is needed or it failed
        public static string ToKorean(string text)
        {
            string hit;
            lock (cache) { if (cache.TryGetValue(text, out hit)) return hit.Length == 0 ? null : hit; }
            string result = "";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Http r = Fetcher.Send("https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ko&dt=t&q=" +
                                      Uri.EscapeDataString(text));
                if (r.Error != null) { LastError = "네트워크 오류"; continue; }
                if (r.Status == 429) { LastError = "번역 요청이 너무 많아요. 잠시 후 다시 시도해 주세요"; Thread.Sleep(400); continue; }
                if (r.Status != 200) { LastError = "번역 서버 오류 (" + r.Status + ")"; continue; }
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
                    string lang = (root.Count > 2 ? root[2] as string : null) ?? "";
                    if (lang == "ko") { result = ""; }
                    else result = sb.ToString().Trim();
                    LastError = "";
                    lock (cache) { if (cache.Count > 3000) cache.Clear(); cache[text] = result; }
                    return result.Length == 0 ? null : result;
                }
                catch { LastError = "번역 응답을 해석하지 못했어요"; }
            }
            return null;
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

            string[] outText = new string[todo.Count];
            ParallelOptions po = new ParallelOptions();
            po.MaxDegreeOfParallelism = 6;
            Parallel.For(0, todo.Count, po, delegate (int i)
            {
                outText[i] = Translator.ToKorean(todo[i].Text);
            });
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
        public const int AppVersion = 1;

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

    // ------------------------------------------------------------------ tray app
    class TrayForm : Form
    {
        Settings st;
        NotifyIcon notify;
        ContextMenuStrip menu;
        ToolStripMenuItem miLive, miAuto, miAutoUpd;
        System.Windows.Forms.Timer liveTimer, updTimer;

        OverlayForm overlay;
        bool liveOn, liveBusy, updBusy, oldCleaned;
        Rectangle liveRect;
        byte[] lastSig;
        int jobId;
        DateTime startTime = DateTime.UtcNow;
        DateTime nextUpd = DateTime.UtcNow.AddSeconds(30);

        public TrayForm()
        {
            st = Settings.Load();
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Size = new Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            IntPtr h = Handle; // 핫키용 핸들 미리 생성

            BuildMenu();
            notify = new NotifyIcon();
            try { notify.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { notify.Icon = SystemIcons.Application; }
            notify.Text = "화면 번역기";
            notify.ContextMenuStrip = menu;
            notify.DoubleClick += delegate { Post(delegate { StartRegion(false); }); };
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
            else
                Notify("화면 번역기", "Ctrl+Alt+T: 영역 번역 / Ctrl+Alt+L: 실시간 / Ctrl+Alt+F: 전체 화면");
            if (OcrService.Languages().Count == 0)
                Notify("OCR 언어팩 없음", "설정 > 시간 및 언어 > 언어 및 지역에서 번역할 언어를 추가해 주세요");
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
            menu.Items.Add(MI("영역 선택해서 번역  (Ctrl+Alt+T)", delegate { Post(delegate { StartRegion(false); }); }));
            miLive = MI("영역 실시간 번역  (Ctrl+Alt+L)", delegate { Post(delegate { if (liveOn) StopAll(); else StartRegion(true); }); });
            menu.Items.Add(miLive);
            menu.Items.Add(MI("현재 화면 전체 번역  (Ctrl+Alt+F)", delegate { Post(StartFull); }));
            menu.Items.Add(MI("번역 지우기 / 실시간 중지  (Ctrl+Alt+X)", delegate { StopAll(); }));
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem iv = new ToolStripMenuItem("실시간 갱신 주기");
            int[] iu = { 1000, 1500, 3000, 5000 };
            string[] iname = { "1초", "1.5초", "3초", "5초" };
            for (int i = 0; i < iu.Length; i++)
            {
                int val = iu[i];
                ToolStripMenuItem it = MI(iname[i], delegate { st.LiveIntervalMs = val; liveTimer.Interval = val; st.Save(); });
                iv.DropDownItems.Add(it);
            }
            menu.Items.Add(iv);

            ToolStripMenuItem langs = MI("설치된 OCR 언어 보기", delegate
            {
                List<string> l = OcrService.Languages();
                MessageBox.Show(l.Count == 0 ? "설치된 OCR 언어가 없어요." : string.Join(", ", l.ToArray()), "OCR 언어");
            });
            menu.Items.Add(langs);

            ToolStripMenuItem upd = new ToolStripMenuItem("업데이트");
            miAutoUpd = MI("자동 업데이트 (6시간마다 확인)", delegate { st.AutoUpdate = !st.AutoUpdate; st.Save(); });
            upd.DropDownItems.Add(miAutoUpd);
            upd.DropDownItems.Add(MI("지금 업데이트 확인", delegate { StartUpdateCheck(true); }));
            upd.DropDownItems.Add(MI("업데이트 주소 설정...", delegate { EditUpdateUrl(); }));
            upd.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem verItem = MI("현재 버전: v" + AppInfo.AppVersion, null);
            verItem.Enabled = false;
            upd.DropDownItems.Add(verItem);
            menu.Items.Add(upd);

            miAuto = MI("Windows 시작 시 자동 실행", delegate { SetAutoStart(!GetAutoStart()); });
            menu.Items.Add(miAuto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MI("종료", delegate { Close(); }));

            menu.Opening += delegate
            {
                miLive.Checked = liveOn;
                miAuto.Checked = GetAutoStart();
                miAutoUpd.Checked = st.AutoUpdate;
            };
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
                    if (on) k.SetValue("ScreenTranslator", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("ScreenTranslator", false);
                }
            }
            catch { }
        }

        // ---------- capture ----------
        static Bitmap CaptureScreen(Rectangle r)
        {
            Bitmap b = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            return b;
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
            Bitmap shot = CaptureScreen(vs);
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
            Bitmap bmp = CaptureScreen(r);
            BeginOnce(r, bmp);
        }

        void BeginOnce(Rectangle rect, Bitmap bmp)
        {
            overlay = new OverlayForm(rect, false);
            overlay.Dismissed += delegate { CloseOverlay(); };
            overlay.SetContent(new List<Item>(), "번역 중...");
            overlay.Show();
            RunJob(bmp, true, ++jobId);
        }

        void BeginLive(Rectangle rect)
        {
            liveRect = rect;
            liveOn = true;
            lastSig = null;
            OcrService.BestLang = null;
            overlay = new OverlayForm(rect, true);
            overlay.SetContent(new List<Item>(), "실시간 번역 시작 (Ctrl+Alt+X 로 중지)");
            overlay.Show();
            liveTimer.Interval = st.LiveIntervalMs;
            liveTimer.Start();
            OnLiveTick(null, EventArgs.Empty);
        }

        void OnLiveTick(object sender, EventArgs e)
        {
            if (!liveOn || liveBusy || overlay == null) return;
            bool hidden = false;
            if (!overlay.ExcludedFromCapture) { overlay.Opacity = 0; hidden = true; Application.DoEvents(); Thread.Sleep(30); }
            Bitmap bmp;
            try { bmp = CaptureScreen(liveRect); }
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
                    string status = "";
                    if (err.Length > 0) status = err;
                    else if (items.Count == 0 && !liveOn) status = "번역할 외국어 텍스트를 찾지 못했어요 (클릭하면 닫혀요)";
                    overlay.SetContent(items, status);
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
                if (manual) Notify("업데이트", "업데이트 주소가 아직 없어요. 우클릭 > 업데이트 > 업데이트 주소 설정");
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
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & start \"\" \"" + exe + "\" --updated");
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
                          "• 끄려면 업데이트 메뉴에서 자동 업데이트 체크를 해제하세요.";
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
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        public static bool JustUpdated;

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            foreach (string a in args) if (a == "--updated") JustUpdated = true;
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
                MessageBox.Show("이미 실행 중이에요.\n화면 오른쪽 아래 트레이(^) 아이콘을 확인해 보세요.", "화면 번역기");
                return;
            }
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayForm());
            GC.KeepAlive(mutex);
        }
    }
}
