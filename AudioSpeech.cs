// AudioSpeech: speech-to-text using Gemini. Press or hold a global hotkey, speak, and the Gemini transcript is pasted into the focused window.
// Build: csc /target:winexe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll AudioSpeech.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AudioSpeech
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            SetProcessDPIAware(); // otherwise Windows bitmap-stretches the windows at 125/150 % and they look blurry
            if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();
            if (args.Length > 1 && args[0] == "--test")
            {
                // Transcribe a 16 kHz mono WAV and write the result to the log (no UI).
                byte[] wav = File.ReadAllBytes(args[1]);
                byte[] pcm = new byte[wav.Length - 44];
                Buffer.BlockCopy(wav, 44, pcm, 0, pcm.Length);
                Log.Write("test: " + Gemini.Transcribe(pcm, Settings.Load()));
                return 0;
            }
            L.Lang = Settings.Load().Language;
            if (args.Length > 0 && args[0] == "--rectest") return SelfTest.Record();
            if (args.Length > 0 && args[0] == "--looktest") return SelfTest.Looks();
            if (args.Length > 0 && args[0] == "--uitest") return SelfTest.Ui();
            bool created;
            using (new Mutex(true, "AudioSpeech_single_instance", out created))
            {
                if (!created) { MessageBox.Show(L.T("AudioSpeech уже запущен (значок в трее).", "AudioSpeech is already running (tray icon)."), "AudioSpeech"); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
            return 0;
        }
    }

    // UI language: every user-visible string is written as L.T("русский", "English").
    static class L
    {
        public static string Lang = "ru";
        public static string T(string ru, string en) { return Lang == "en" ? en : ru; }
    }

    static class Log
    {
        public static void Write(string m)
        {
            try { Directory.CreateDirectory(Settings.Dir); File.AppendAllText(Path.Combine(Settings.Dir, "audiospeech.log"), DateTime.Now.ToString("s") + " " + m + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
    }

    class Settings
    {
        public string Hotkey = "Ctrl+Alt+Space";
        public string Mode = "hold_or_toggle"; // or "toggle", "hold"
        public string Model = "gemini-3.5-flash";
        public string ApiKey = "";
        public string Theme = "dark"; // or "light"
        public string Accent = "#FF8AC4";
        public string Language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        public int KeepRecordings = 10;
        public string Hint = ""; // extra words for the transcription prompt: usual languages, names, terms // older recordings are deleted, like Handy's history limit

        public Color AccentColor
        {
            get { try { return ColorTranslator.FromHtml(Accent); } catch { return Color.FromArgb(255, 138, 196); } }
        }

        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioSpeech");
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public static Settings Load()
        {
            try { if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new Settings(); }
            catch (Exception e) { Log.Write("settings: " + e.Message); }
            return new Settings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
        }
    }

    struct Hotkey
    {
        public bool Ctrl, Alt, Shift, Win;
        public Keys Key;

        public static Hotkey Parse(string s)
        {
            var h = new Hotkey();
            foreach (string raw in (s ?? "").Split('+'))
            {
                string p = raw.Trim();
                Keys k;
                if (p == "Ctrl") h.Ctrl = true;
                else if (p == "Alt") h.Alt = true;
                else if (p == "Shift") h.Shift = true;
                else if (p == "Win") h.Win = true;
                else if (p.Length > 0 && Enum.TryParse(p, true, out k)) h.Key = k;
            }
            return h.Key == Keys.None ? Parse(new Settings().Hotkey) : h;
        }

        public bool SameModifiers(Hotkey o) { return Ctrl == o.Ctrl && Alt == o.Alt && Shift == o.Shift && Win == o.Win; }

        public override string ToString()
        {
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            parts.Add(Key.ToString());
            return string.Join("+", parts);
        }
    }

    static class Native
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
        public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }

        public static Hotkey Modifiers()
        {
            return new Hotkey { Ctrl = Down(0x11), Alt = Down(0x12), Shift = Down(0x10), Win = Down(0x5B) || Down(0x5C) };
        }

        public static bool AnyModifierDown() { var m = Modifiers(); return m.Ctrl || m.Alt || m.Shift || m.Win; }

        public static void CtrlV()
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            Tap(0x56);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
        }
    }

    class App : ApplicationContext
    {
        enum State { Idle, Recording, Working }
        public static readonly string[] Modes = { "hold_or_toggle", "toggle", "hold" };
        public static string RecordingsDir { get { return Path.Combine(Settings.Dir, "recordings"); } }

        public static FileInfo[] Recordings() { return Recordings(RecordingsDir); }

        static FileInfo[] Recordings(string path)
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return new FileInfo[0];
            FileInfo[] files = dir.GetFiles("rec-*.wav");
            Array.Sort(files, (a, b) => string.CompareOrdinal(b.Name, a.Name)); // names are timestamps: newest first
            return files;
        }

        // Saves the recording (rec-<time>.wav) and deletes the oldest beyond the limit, with their transcripts.
        string SaveRecording(byte[] pcm)
        {
            try
            {
                Directory.CreateDirectory(RecordingsDir);
                string path = Path.Combine(RecordingsDir, "rec-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".wav");
                File.WriteAllBytes(path, Gemini.WavFile(pcm, 0, pcm.Length));
                Prune(RecordingsDir, Settings.KeepRecordings);
                return path;
            }
            catch (Exception e) { Log.Write("save wav: " + e.Message); return null; }
        }

        public static void Prune(string dir, int keep)
        {
            FileInfo[] all = Recordings(dir);
            for (int i = Math.Max(1, keep); i < all.Length; i++)
            {
                File.Delete(all[i].FullName);
                File.Delete(Path.ChangeExtension(all[i].FullName, ".txt"));
            }
        }

        public static void SaveTranscript(string wavPath, string text)
        {
            if (wavPath == null || string.IsNullOrEmpty(text)) return;
            try { File.WriteAllText(Path.ChangeExtension(wavPath, ".txt"), text, Encoding.UTF8); } catch (Exception e) { Log.Write("save txt: " + e.Message); }
        }

        public Settings Settings;
        Hotkey hotkey;
        readonly NotifyIcon tray;
        readonly Overlay overlay;
        Recorder recorder;
        State state = State.Idle;
        int generation;
        bool keyHeld, ignoreRelease;
        DateTime pressedAt;
        IntPtr hook;
        readonly Native.HookProc hookProc; // kept in a field so the GC does not collect the callback
        MainWindow window;
        public static bool Testing; // --uitest: no keyboard hook, no balloon
        public Action<Hotkey> Capture; // set while the settings window records a new hotkey

        public App()
        {
            Settings = Settings.Load();
            L.Lang = Settings.Language;
            hotkey = Hotkey.Parse(Settings.Hotkey);
            overlay = new Overlay();
            overlay.ApplyLook(Settings);
            overlay.CancelClicked += Cancel;

            tray = new NotifyIcon { Icon = MakeIcon(Settings.AccentColor), Visible = true, ContextMenuStrip = BuildMenu() };
            tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ShowWindow(0); };
            UpdateTrayText();

            hookProc = HookCallback;
            if (Testing) return;
            hook = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, hookProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Notify(L.T("Не удалось перехватить клавиатуру, горячая клавиша не работает.", "Could not hook the keyboard; the hotkey will not work."), ToolTipIcon.Error);
            else Notify(L.T("Диктовка: ", "Dictation: ") + hotkey + L.T(". Отмена: Esc.", ". Cancel: Esc."), ToolTipIcon.Info);
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(L.T("История записей", "Recordings"), null, delegate { ShowWindow(0); });
            menu.Items.Add(L.T("Настройки", "Settings"), null, delegate { ShowWindow(1); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("Выход", "Exit"), null, delegate { ExitThread(); });
            return menu;
        }

        protected override void ExitThreadCore()
        {
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            if (recorder != null) recorder.Stop();
            tray.Visible = false;
            base.ExitThreadCore();
        }

        void UpdateTrayText() { tray.Text = "AudioSpeech (" + hotkey + ")"; }

        void Notify(string text, ToolTipIcon icon)
        {
            tray.ShowBalloonTip(4000, "AudioSpeech", text.Length > 250 ? text.Substring(0, 250) : text, icon);
        }

        // Runs on the UI thread inside the hook; keep it fast and defer real work with BeginInvoke.
        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                var vk = (Keys)Marshal.ReadInt32(lParam);
                bool injected = (Marshal.ReadInt32(lParam, 8) & 0x10) != 0;
                bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                if (!injected && (down || up) && HandleKey(vk, down)) return (IntPtr)1;
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        bool HandleKey(Keys vk, bool down)
        {
            if (IsModifier(vk)) return false;
            if (Capture != null)
            {
                if (!down) return true;
                Hotkey h = Native.Modifiers();
                h.Key = vk;
                var cb = Capture;
                Capture = null;
                overlay.BeginInvoke(new Action(() => cb(h)));
                return true;
            }
            if (vk == hotkey.Key && (keyHeld || (down && Native.Modifiers().SameModifiers(hotkey))))
            {
                if (down && (hotkey.Alt || hotkey.Win)) Native.Tap(0xE8); // unassigned key: stops Alt/Win release from opening a menu
                if (down && !keyHeld)
                {
                    keyHeld = true;
                    pressedAt = DateTime.UtcNow;
                    overlay.BeginInvoke(new Action(HotkeyDown));
                }
                else if (!down)
                {
                    keyHeld = false;
                    double held = (DateTime.UtcNow - pressedAt).TotalMilliseconds;
                    overlay.BeginInvoke(new Action(() => HotkeyUp(held)));
                }
                return true;
            }
            if (vk == Keys.Escape && down && state != State.Idle) { overlay.BeginInvoke(new Action(Cancel)); return true; }
            return false;
        }

        static bool IsModifier(Keys k)
        {
            switch (k)
            {
                case Keys.ControlKey: case Keys.LControlKey: case Keys.RControlKey:
                case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey:
                case Keys.Menu: case Keys.LMenu: case Keys.RMenu:
                case Keys.LWin: case Keys.RWin:
                    return true;
            }
            return false;
        }

        // Same behaviour as Handy's "hold or toggle": a short tap toggles, holding >= 300 ms is push-to-talk.
        void HotkeyDown()
        {
            if (state == State.Idle) StartRecording();
            else if (state == State.Recording && Settings.Mode != "hold") { ignoreRelease = true; StopRecording(); }
        }

        void HotkeyUp(double heldMs)
        {
            if (ignoreRelease) { ignoreRelease = false; return; }
            if (state != State.Recording) return;
            if (Settings.Mode == "hold" || (Settings.Mode == "hold_or_toggle" && heldMs >= 300)) StopRecording();
        }

        void StartRecording()
        {
            try { recorder = new Recorder(); recorder.Start(); }
            catch (Exception e) { recorder = null; Log.Write("mic: " + e); Notify(L.T("Микрофон: ", "Microphone: ") + e.Message, ToolTipIcon.Error); return; }
            state = State.Recording;
            generation++;
            overlay.ShowRecording(() => recorder != null ? recorder.Level : 0f);
        }

        void StopRecording()
        {
            byte[] pcm = recorder.Stop();
            recorder = null;
            state = State.Working;
            int gen = generation;
            overlay.ShowWorking();
            // Keep the audio so a failed request can be retried from the tray menu.
            string wavPath = SaveRecording(pcm);
            RefreshHistory();
            Settings s = Settings;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text = null, error = null;
                try { text = Gemini.Transcribe(pcm, s); SaveTranscript(wavPath, text); }
                catch (Exception e) { error = e.Message; Log.Write("transcribe: " + e); }
                // Pasting while the hotkey modifiers are still held would send e.g. Ctrl+Alt+V.
                for (int i = 0; i < 150 && Native.AnyModifierDown(); i++) Thread.Sleep(20);
                overlay.BeginInvoke(new Action(() => Finish(gen, text, error)));
            });
        }

        void Finish(int gen, string text, string error)
        {
            RefreshHistory();
            if (gen != generation || state != State.Working) return; // cancelled meanwhile
            state = State.Idle;
            if (error != null)
            {
                overlay.ShowMessage(L.T("Ошибка Gemini", "Gemini error"), 2500);
                Notify(error + L.T("\nЗапись сохранена: откройте историю и расшифруйте её снова.", "\nThe recording is saved: open the history and transcribe it again."), ToolTipIcon.Error);
                return;
            }
            overlay.HideOverlay();
            if (!string.IsNullOrEmpty(text)) Paster.Paste(text);
        }

        void Cancel()
        {
            if (recorder != null) { recorder.Stop(); recorder = null; }
            generation++;
            state = State.Idle;
            ignoreRelease = keyHeld;
            overlay.HideOverlay();
        }

        public Icon TrayIcon { get { return tray.Icon; } }

        public static void OpenFolder()
        {
            Directory.CreateDirectory(RecordingsDir);
            // Via the shell, not a new explorer.exe: that one reported the folder as unavailable.
            Process.Start(new ProcessStartInfo { FileName = RecordingsDir, UseShellExecute = true });
        }

        public void ShowWindow(int tab)
        {
            if (window == null || window.IsDisposed)
            {
                window = new MainWindow(this, tab);
                window.FormClosed += delegate { Capture = null; window = null; };
                window.Show();
            }
            else
            {
                window.ShowTab(tab);
                if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            }
            window.Activate();
        }

        void RefreshHistory() { if (window != null && !window.IsDisposed) window.RefreshHistory(); }

        // Transcribes a saved recording again (history "retry"); done runs on the UI thread with an error or null.
        public void Retranscribe(string wavPath, Action<string> done)
        {
            Settings s = Settings;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    byte[] wav = File.ReadAllBytes(wavPath);
                    byte[] pcm = new byte[wav.Length - 44];
                    Buffer.BlockCopy(wav, 44, pcm, 0, pcm.Length);
                    string text = Gemini.Transcribe(pcm, s);
                    if (text.Length == 0) error = L.T("В записи не найдено речи.", "No speech found in the recording.");
                    SaveTranscript(wavPath, text);
                }
                catch (Exception e) { error = e.Message; Log.Write("retry: " + e); }
                overlay.BeginInvoke(new Action(() => done(error)));
            });
        }

        public void ApplySettings()
        {
            Settings.Save();
            hotkey = Hotkey.Parse(Settings.Hotkey);
            keyHeld = false;
            overlay.ApplyLook(Settings);
            L.Lang = Settings.Language;
            ContextMenuStrip oldMenu = tray.ContextMenuStrip;
            tray.ContextMenuStrip = BuildMenu();
            oldMenu.Dispose();
            Icon oldIcon = tray.Icon;
            tray.Icon = MakeIcon(Settings.AccentColor);
            oldIcon.Dispose();
            UpdateTrayText();
        }

        static Icon MakeIcon(Color accent)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            using (var dark = new SolidBrush(Color.FromArgb(40, 40, 40)))
            using (var pink = new SolidBrush(accent))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(dark, 1, 1, 30, 30);
                g.FillEllipse(pink, 9, 9, 14, 14);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    static class Paster
    {
        public static void Paste(string text)
        {
            // Put the user's clipboard back afterwards (text or image; other formats are not kept).
            string savedText = null;
            Image savedImage = null;
            try
            {
                if (Clipboard.ContainsText()) savedText = Clipboard.GetText();
                else if (Clipboard.ContainsImage()) savedImage = Clipboard.GetImage();
            }
            catch { }
            try { Clipboard.SetDataObject(text, true, 10, 50); }
            catch (Exception e) { Log.Write("clipboard: " + e.Message); return; }
            Native.CtrlV();
            var t = new System.Windows.Forms.Timer { Interval = 600 };
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                try
                {
                    if (savedText != null) Clipboard.SetDataObject(savedText, true, 10, 50);
                    else if (savedImage != null) Clipboard.SetImage(savedImage);
                }
                catch { }
            };
            t.Start();
        }
    }

    class Recorder
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WaveFormat { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }

        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int device, ref WaveFormat fmt, IntPtr callback, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);

        const int Buffers = 8, BufBytes = 1600; // 50 ms each at 16 kHz mono 16-bit
        static readonly int HdrSize = Marshal.SizeOf(typeof(WaveHdr));
        static readonly int OffRecorded = (int)Marshal.OffsetOf(typeof(WaveHdr), "dwBytesRecorded");
        static readonly int OffFlags = (int)Marshal.OffsetOf(typeof(WaveHdr), "dwFlags");

        IntPtr h;
        readonly IntPtr[] hdrs = new IntPtr[Buffers];
        int next;
        readonly AutoResetEvent ev = new AutoResetEvent(false);
        readonly MemoryStream pcm = new MemoryStream();
        readonly byte[] tmp = new byte[BufBytes];
        Thread thread;
        volatile bool running;
        public volatile float Level;

        public void Start()
        {
            var fmt = new WaveFormat { wFormatTag = 1, nChannels = 1, nSamplesPerSec = 16000, nAvgBytesPerSec = 32000, nBlockAlign = 2, wBitsPerSample = 16 };
            int r = waveInOpen(out h, -1 /* default device */, ref fmt, ev.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000 /* CALLBACK_EVENT */);
            if (r != 0) throw new Exception(L.T("не удалось открыть микрофон (код ", "could not open the microphone (code ") + r + ")");
            for (int i = 0; i < Buffers; i++)
            {
                var hdr = new WaveHdr { lpData = Marshal.AllocHGlobal(BufBytes), dwBufferLength = BufBytes };
                hdrs[i] = Marshal.AllocHGlobal(HdrSize);
                Marshal.StructureToPtr(hdr, hdrs[i], false);
                waveInPrepareHeader(h, hdrs[i], HdrSize);
                waveInAddBuffer(h, hdrs[i], HdrSize);
            }
            running = true;
            thread = new Thread(() => { while (running) { ev.WaitOne(100); Harvest(true); } }) { IsBackground = true };
            thread.Start();
            waveInStart(h);
        }

        // Take filled buffers in queue order and hand them back to the driver.
        void Harvest(bool requeue)
        {
            for (int k = 0; k < Buffers; k++)
            {
                IntPtr hp = hdrs[next];
                int flags = Marshal.ReadInt32(hp, OffFlags);
                if ((flags & 1 /* WHDR_DONE */) == 0) return;
                int n = Marshal.ReadInt32(hp, OffRecorded);
                Marshal.Copy(Marshal.ReadIntPtr(hp), tmp, 0, n);
                pcm.Write(tmp, 0, n);
                if (n > 0) Level = Rms(tmp, n);
                Marshal.WriteInt32(hp, OffFlags, flags & ~1);
                if (requeue) waveInAddBuffer(h, hp, HdrSize);
                next = (next + 1) % Buffers;
            }
        }

        static float Rms(byte[] b, int n)
        {
            double sum = 0;
            for (int i = 0; i + 1 < n; i += 2) { double v = (short)(b[i] | (b[i + 1] << 8)); sum += v * v; }
            double rms = Math.Sqrt(sum / Math.Max(1, n / 2));
            return (float)Math.Pow(Math.Min(1.0, rms / 2500), 0.6);
        }

        public byte[] Stop()
        {
            running = false;
            thread.Join();
            waveInReset(h); // marks the queued buffers done, including the partly filled one
            Harvest(false);
            foreach (IntPtr hp in hdrs)
            {
                waveInUnprepareHeader(h, hp, HdrSize);
                Marshal.FreeHGlobal(Marshal.ReadIntPtr(hp));
                Marshal.FreeHGlobal(hp);
            }
            waveInClose(h);
            return pcm.ToArray();
        }
    }

    static class Gemini
    {
        const int Rate = 16000;
        public const int ChunkBytes = Rate * 2 * 240; // 4 min per request, ~10 MB base64, under the 20 MB inline limit
        // Measured 2026-11-15: 3.5-flash ~2.7 s, flash-lite-latest ~1.9 s; flash-latest / 3.7 / 3.8 hung or returned 503.
        static readonly string[] Fallbacks = { "gemini-3.5-flash", "gemini-flash-lite-latest", "gemini-flash-latest" };
        const string Prompt = "Transcribe this voice recording verbatim. Keep every word in the language and script it was spoken in; the speaker may switch languages mid-sentence. Add normal punctuation and capitalization. Do not translate, summarize, answer, or comment. Output only the transcript. If there is no speech, output nothing.";

        public static byte[] WavFile(byte[] pcm, int offset, int count)
        {
            var ms = new MemoryStream(44 + count);
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + count); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(count);
            w.Write(pcm, offset, count);
            return ms.ToArray();
        }

        static bool IsSilent(byte[] pcm)
        {
            for (int i = 0; i + 1 < pcm.Length; i += 2) if (Math.Abs((int)(short)(pcm[i] | (pcm[i + 1] << 8))) > 200) return false;
            return true;
        }

        public static string Transcribe(byte[] pcm, Settings s)
        {
            if (pcm.Length < Rate * 2 * 3 / 10 || IsSilent(pcm)) return ""; // under 0.3 s or silence: skip the API call
            string key = ApiKey(s);
            var models = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.Model)) models.Add(s.Model.Trim());
            foreach (string m in Fallbacks) if (!models.Contains(m)) models.Add(m);
            var texts = new List<string>();
            for (int off = 0; off < pcm.Length; off += ChunkBytes)
            {
                int n = Math.Min(ChunkBytes, pcm.Length - off);
                string used;
                string t = Piece(WavFile(pcm, off, n), n, key, string.IsNullOrWhiteSpace(s.Hint) ? Prompt : Prompt + " " + s.Hint.Trim(), models, out used);
                models = new List<string> { used }; // the first model that answered serves the rest
                if (t.Length > 0) texts.Add(t);
            }
            return Regex.Replace(string.Join(" ", texts), @"\s+", " ").Trim();
        }

        static string Piece(byte[] wav, int pcmBytes, string key, string prompt, List<string> models, out string used)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string body = json.Serialize(new Dictionary<string, object> {
                { "contents", new object[] { new Dictionary<string, object> {
                    { "role", "user" },
                    { "parts", new object[] {
                        new Dictionary<string, object> { { "text", prompt } },
                        new Dictionary<string, object> { { "inline_data", new Dictionary<string, object> { { "mime_type", "audio/wav" }, { "data", Convert.ToBase64String(wav) } } } } } } } } } });
            byte[] data = Encoding.UTF8.GetBytes(body);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false;
            string last = "";
            foreach (string model in models)
            {
                var req = (HttpWebRequest)WebRequest.Create("https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers["x-goog-api-key"] = key;
                req.Timeout = req.ReadWriteTimeout = 9000 + pcmBytes / 320; // +1 s per 10 s of audio
                try
                {
                    using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        used = model;
                        return ResponseText(json.Deserialize<Dictionary<string, object>>(r.ReadToEnd()));
                    }
                }
                catch (WebException e)
                {
                    var resp = e.Response as HttpWebResponse;
                    if (resp == null) { last = model + ": " + e.Status; Log.Write("next model, " + last); continue; }
                    using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) last = model + ": " + r.ReadToEnd();
                    int code = (int)resp.StatusCode;
                    // Unknown model, overloaded or rate-limited: try the next one. A rejected key is final.
                    if (code == 404 || code == 429 || code >= 500) { Log.Write("next model, HTTP " + code + " " + model); continue; }
                    if (code == 401 || code == 403 || (code == 400 && last.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0))
                        throw new Exception(L.T("Ключ Gemini отклонён. Проверьте его в настройках.", "The Gemini key was rejected. Check it in Settings."));
                    throw new Exception("Gemini: HTTP " + code + " " + last.Substring(0, Math.Min(200, last.Length)));
                }
            }
            throw new Exception(L.T("Ни одна модель Gemini не ответила (", "No Gemini model answered (") + last.Substring(0, Math.Min(160, last.Length)) + ")");
        }

        static string ResponseText(Dictionary<string, object> body)
        {
            var cands = body.ContainsKey("candidates") ? body["candidates"] as System.Collections.ArrayList : null;
            if (cands == null || cands.Count == 0) return "";
            var content = ((Dictionary<string, object>)cands[0]).ContainsKey("content") ? ((Dictionary<string, object>)cands[0])["content"] as Dictionary<string, object> : null;
            var parts = content != null && content.ContainsKey("parts") ? content["parts"] as System.Collections.ArrayList : null;
            if (parts == null) return "";
            var sb = new StringBuilder();
            foreach (Dictionary<string, object> part in parts)
                if (!(part.ContainsKey("thought") && true.Equals(part["thought"])) && part.ContainsKey("text")) sb.Append(part["text"]);
            return sb.ToString().Trim();
        }

        static string ApiKey(Settings s)
        {
            if (!string.IsNullOrWhiteSpace(s.ApiKey)) return s.ApiKey.Trim();
            string env = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            // The Google key LearnHarness keeps under Settings -> Keys.
            string auth = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LearnHarness", "auth.json");
            if (File.Exists(auth))
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(auth));
                string k = d != null && d.ContainsKey("GEMINI_API_KEY") ? d["GEMINI_API_KEY"] as string : null;
                if (!string.IsNullOrWhiteSpace(k)) return k.Trim();
            }
            throw new Exception(L.T("Нет ключа Gemini: укажите его в настройках.", "No Gemini key: enter it in Settings."));
        }
    }

    static class Dpi
    {
        public static readonly float S = Read();
        static float Read() { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
        public static int Px(float v) { return (int)Math.Round(v * S); }
    }

    // Per-pixel-alpha (layered) window: anti-aliased edges and a soft shadow instead of a jagged Region.
    class Overlay : Form
    {
        enum Look { Recording, Working, Message }
        public Color Accent = Color.FromArgb(255, 138, 196);
        public bool Light;
        const float Pad = 14, PillW = 236, PillH = 44; // logical px, scaled by Dpi.S
        Look look;
        string message = "";
        Func<float> level = () => 0f;
        readonly float[] bars = new float[13];
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 33 };
        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();
        int tick;
        float opacity;      // 0..1, animated for fade in/out
        bool fadingOut, hover;
        public event Action CancelClicked;

        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT dst, ref SZ size, IntPtr hdcSrc, ref PT src, int key, ref Blend blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(Dpi.Px(PillW + 2 * Pad), Dpi.Px(PillH + 2 * Pad));
            anim.Tick += delegate { Step(); Render(); };
            hideTimer.Tick += delegate { HideOverlay(); };
            CreateHandle(); // BeginInvoke from the keyboard hook needs a handle before the first Show
        }

        // No activation and topmost via styles: the TopMost property would steal focus from the window we paste into.
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x80000 /* LAYERED */ | 0x80 /* TOOLWINDOW */ | 0x8 /* TOPMOST */; return cp; }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        RectangleF PillRect { get { return new RectangleF(Pad, Pad, PillW, PillH); } }
        RectangleF CloseRectF { get { return new RectangleF(Pad + PillW - 8 - 28, Pad + (PillH - 28) / 2, 28, 28); } }
        Rectangle CloseRect { get { RectangleF r = CloseRectF; return new Rectangle(Dpi.Px(r.X), Dpi.Px(r.Y), Dpi.Px(r.Width), Dpi.Px(r.Height)); } }

        public void FeedForTest(float v) { Array.Copy(bars, 1, bars, 0, bars.Length - 1); bars[bars.Length - 1] = v; }

        public void ApplyLook(Settings s) { Accent = s.AccentColor; Light = s.Theme == "light"; if (Visible) Render(); }

        public void ShowRecording(Func<float> lv) { level = lv; look = Look.Recording; Array.Clear(bars, 0, bars.Length); Present(); }
        public void ShowWorking() { look = Look.Working; Present(); }
        public void ShowMessage(string text, int ms) { message = text; look = Look.Message; Present(); hideTimer.Interval = ms; hideTimer.Start(); }
        public void HideOverlay() { hideTimer.Stop(); if (Visible) fadingOut = true; }

        void Present()
        {
            hideTimer.Stop();
            fadingOut = false;
            if (!Visible)
            {
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - Dpi.Px(36));
                opacity = 0;
                Render();
                Show();
            }
            anim.Start();
        }

        void Step()
        {
            tick++;
            opacity = fadingOut ? Math.Max(0, opacity - 0.2f) : Math.Min(1, opacity + 0.2f);
            if (fadingOut && opacity <= 0) { anim.Stop(); fadingOut = false; Hide(); return; }
            if (look != Look.Recording) return;
            Array.Copy(bars, 1, bars, 0, bars.Length - 1);
            bars[bars.Length - 1] = level();
        }

        static GraphicsPath Pill(RectangleF r)
        {
            var p = new GraphicsPath();
            float d = Math.Min(r.Width, r.Height);
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        void Draw(Graphics g)
        {
            g.ScaleTransform(Dpi.S, Dpi.S);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            RectangleF pill = PillRect;

            // Soft shadow: stacked translucent pills, offset slightly down.
            using (var sb = new SolidBrush(Color.FromArgb(Light ? 5 : 7, 0, 0, 0)))
                for (int i = 10; i >= 1; i--)
                {
                    RectangleF r = pill;
                    r.Inflate(i, i);
                    r.Offset(0, 3);
                    using (GraphicsPath p = Pill(r)) g.FillPath(sb, p);
                }

            using (GraphicsPath p = Pill(pill))
            {
                using (var body = Light
                    ? new LinearGradientBrush(pill, Color.FromArgb(255, 255, 255), Color.FromArgb(242, 242, 245), LinearGradientMode.Vertical)
                    : new LinearGradientBrush(pill, Color.FromArgb(38, 38, 42), Color.FromArgb(26, 26, 29), LinearGradientMode.Vertical)) g.FillPath(body, p);
            }
            RectangleF edge = pill;
            edge.Inflate(-0.5f, -0.5f);
            using (GraphicsPath p = Pill(edge))
            using (var pen = new Pen(Light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255), 1f)) g.DrawPath(pen, p);

            float cy = pill.Y + pill.Height / 2f;
            float dotX = pill.X + 22;
            Color dot = look == Look.Message ? Color.FromArgb(255, 176, 92) : Accent;
            float pulse = look == Look.Working ? (float)(0.5 + 0.5 * Math.Sin(tick / 5.0)) : look == Look.Recording ? Math.Min(1f, bars[bars.Length - 1] * 1.5f) : 0f;
            using (var halo = new SolidBrush(Color.FromArgb((int)(40 + 50 * pulse), dot))) g.FillEllipse(halo, dotX - 9, cy - 9, 18, 18);
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, dotX - 5, cy - 5, 10, 10);

            RectangleF close = CloseRectF;
            if (look == Look.Message)
            {
                using (var f = new Font("Segoe UI", 13f, GraphicsUnit.Pixel))
                using (var tb = new SolidBrush(Light ? Color.FromArgb(30, 30, 34) : Color.FromArgb(232, 232, 236)))
                using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias; // ClearType needs an opaque background
                    g.DrawString(message, f, tb, new RectangleF(dotX + 16, pill.Y, close.X - dotX - 24, pill.Height), sf);
                }
            }
            else
            {
                float left = dotX + 20, right = close.X - 12;
                float step = (right - left) / (bars.Length - 1);
                using (var pen = new Pen(Accent, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < bars.Length; i++)
                    {
                        float v = look == Look.Recording ? bars[i] : (float)(0.25 + 0.25 * Math.Sin(tick / 4.0 - i * 0.55));
                        float half = 0.01f + v * 11f;
                        float x = left + i * step;
                        // Older samples fade toward the left like a trail.
                        pen.Color = Color.FromArgb(look == Look.Recording ? 110 + 145 * i / (bars.Length - 1) : 200, Accent);
                        g.DrawLine(pen, x, cy - half, x, cy + half);
                    }
            }

            Color ink = Light ? Color.FromArgb(40, 40, 46) : Color.FromArgb(236, 236, 240);
            using (var b = new SolidBrush(Light ? Color.FromArgb(hover ? 30 : 14, 0, 0, 0) : Color.FromArgb(hover ? 46 : 22, 255, 255, 255))) g.FillEllipse(b, close);
            using (var pen = new Pen(Color.FromArgb(hover ? 255 : 200, ink), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = close.X + close.Width / 2f, ccy = close.Y + close.Height / 2f, s = 4.5f;
                g.DrawLine(pen, cx - s, ccy - s, cx + s, ccy + s);
                g.DrawLine(pen, cx - s, ccy + s, cx + s, ccy - s);
            }
        }

        void Render()
        {
            using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); Draw(g); }
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
                var dst = new PT { X = Left, Y = Top };
                var size = new SZ { W = Width, H = Height };
                var src = new PT();
                var blend = new Blend { Op = 0, Alpha = (byte)(255 * opacity), Format = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
                SelectObject(mem, old);
                DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // For the --rectest check: the same pixels the window shows, on a dark backdrop.
        public Bitmap Snapshot()
        {
            var bmp = new Bitmap(Width, Height);
            using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Light ? Color.FromArgb(200, 205, 210) : Color.FromArgb(60, 62, 64)); Draw(g); }
            return bmp;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!CloseRect.Contains(e.Location)) return;
            if (look == Look.Message) HideOverlay();
            else if (CancelClicked != null) CancelClicked();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = CloseRect.Contains(e.Location);
            if (h != hover) { hover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Render(); }
        }

        protected override void OnMouseLeave(EventArgs e) { if (hover) { hover = false; Render(); } }
    }

    static class SelfTest
    {
        public static int Run()
        {
            var errors = new List<string>();
            Hotkey h = Hotkey.Parse("Ctrl+Alt+Space");
            if (!(h.Ctrl && h.Alt && !h.Shift && h.Key == Keys.Space) || h.ToString() != "Ctrl+Alt+Space") errors.Add("parse Ctrl+Alt+Space");
            if (Hotkey.Parse("Win+Shift+F9").ToString() != "Shift+Win+F9") errors.Add("parse Win+Shift+F9");
            if (Hotkey.Parse("garbage").Key != Keys.Space) errors.Add("fallback to default");
            byte[] wav = Gemini.WavFile(new byte[100], 0, 100);
            if (wav.Length != 144 || BitConverter.ToInt32(wav, 40) != 100 || BitConverter.ToInt32(wav, 24) != 16000) errors.Add("wav header");
            string tmp = Path.Combine(Path.GetTempPath(), "gd-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            for (int i = 0; i < 12; i++) { File.WriteAllText(Path.Combine(tmp, "rec-202611" + (10 + i) + "-120000-000.wav"), ""); File.WriteAllText(Path.Combine(tmp, "rec-202611" + (10 + i) + "-120000-000.txt"), ""); }
            App.Prune(tmp, 10);
            if (Directory.GetFiles(tmp, "*.wav").Length != 10 || Directory.GetFiles(tmp, "*.txt").Length != 10 || File.Exists(Path.Combine(tmp, "rec-20261111-120000-000.wav")) || !File.Exists(Path.Combine(tmp, "rec-20261121-120000-000.wav")))
                errors.Add("prune keeps newest 10");
            Directory.Delete(tmp, true);
            Log.Write(errors.Count == 0 ? "selftest ok" : "selftest FAILED: " + string.Join(", ", errors));
            return errors.Count == 0 ? 0 : 1;
        }

        // Renders the panel in both themes to PNGs in the settings folder, for checking the look.
        public static int Looks()
        {
            string[][] looks = { new[] { "dark", "#FF8AC4" }, new[] { "light", "#3B82F6" } };
            foreach (string[] l in looks)
            {
                var ov = new Overlay();
                ov.ApplyLook(new Settings { Theme = l[0], Accent = l[1] });
                ov.ShowRecording(() => 0f);
                for (int i = 0; i < 13; i++) ov.FeedForTest((float)Math.Abs(Math.Sin(i * 0.9)) * 0.9f);
                using (Bitmap b = ov.Snapshot()) b.Save(Path.Combine(Settings.Dir, "look-" + l[0] + ".png"));
                ov.Close();
            }
            return 0;
        }

        // Renders both tabs of the main window to PNGs in the settings folder.
        public static int Ui()
        {
            App.Testing = true;
            var app = new App();
            for (int tab = 0; tab < 2; tab++)
            {
                var w = new MainWindow(app, tab);
                w.Show();
                Application.DoEvents();
                using (var bmp = new Bitmap(w.Width, w.Height))
                {
                    w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
                    bmp.Save(Path.Combine(Settings.Dir, "ui-" + tab + ".png"));
                }
                w.Close();
            }
            app.ExitThread();
            return 0;
        }

        // Records 3 s from the default mic with the overlay shown, saves a screenshot of it and logs what was captured.
        public static int Record()
        {
            var rec = new Recorder();
            rec.Start();
            var ov = new Overlay();
            float max = 0;
            ov.ShowRecording(() => { max = Math.Max(max, rec.Level); return rec.Level; });
            var t0 = DateTime.UtcNow;
            bool shot = false;
            while ((DateTime.UtcNow - t0).TotalSeconds < 3)
            {
                Application.DoEvents();
                Thread.Sleep(20);
                if (!shot && (DateTime.UtcNow - t0).TotalSeconds > 2)
                {
                    shot = true;
                    using (Bitmap bmp = ov.Snapshot())
                    {
                        bmp.Save(Path.Combine(Settings.Dir, "overlay-test.png"));
                    }
                }
            }
            byte[] pcm = rec.Stop();
            ov.HideOverlay();
            File.WriteAllBytes(Path.Combine(Settings.Dir, "rectest.wav"), Gemini.WavFile(pcm, 0, pcm.Length));
            Log.Write("rectest: " + pcm.Length + " bytes (" + (pcm.Length / 32000.0).ToString("0.00") + " s), max level " + max.ToString("0.00"));
            return 0;
        }
    }
}
