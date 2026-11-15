// AudioSpeech main window: recordings history with a player, and settings. Everything is custom-drawn
// to match the floating panel; colours come from the panel theme and accent colour.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AudioSpeech
{
    class Theme
    {
        public bool Dark;
        public Color Bg, Surface, Hover, Border, Text, Dim, Accent, OnAccent;
        public Font Body, Small, Title, Icons;

        public static Theme From(Settings s)
        {
            var t = new Theme { Dark = s.Theme != "light", Accent = s.AccentColor };
            if (t.Dark)
            {
                t.Bg = Color.FromArgb(24, 24, 27); t.Surface = Color.FromArgb(35, 35, 40); t.Hover = Color.FromArgb(46, 46, 52);
                t.Border = Color.FromArgb(58, 58, 65); t.Text = Color.FromArgb(236, 236, 240); t.Dim = Color.FromArgb(148, 148, 158);
            }
            else
            {
                t.Bg = Color.FromArgb(244, 244, 247); t.Surface = Color.White; t.Hover = Color.FromArgb(234, 234, 240);
                t.Border = Color.FromArgb(222, 222, 229); t.Text = Color.FromArgb(28, 28, 32); t.Dim = Color.FromArgb(108, 108, 118);
            }
            t.OnAccent = 0.299 * t.Accent.R + 0.587 * t.Accent.G + 0.114 * t.Accent.B > 165 ? Color.FromArgb(22, 22, 26) : Color.White;
            string ui = Installed("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            t.Body = new Font(ui, 10f);
            t.Small = new Font(ui, 8.75f);
            t.Title = new Font(Installed("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI", 15f, FontStyle.Bold);
            t.Icons = new Font(Installed("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 11f);
            return t;
        }

        static bool Installed(string family) { using (var f = new Font(family, 10f)) return f.Name == family; }
    }

    static class Draw
    {
        public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        public const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, Color c, RectangleF r, float radius)
        {
            using (GraphicsPath p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, Color c, RectangleF r, float radius, float width)
        {
            using (GraphicsPath p = Round(r, radius)) using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static RectangleF Inset(Rectangle r) { return new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1.5f, r.Height - 1.5f); }
    }

    // Base for custom-drawn controls: double-buffered, tracks the mouse for hover effects.
    class Ctl : Control
    {
        protected readonly Theme T;
        protected Point Mouse = new Point(-9999, -9999);

        public Ctl(Theme t)
        {
            T = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg;
            Font = t.Body;
        }

        protected Graphics Begin(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            return e.Graphics;
        }

        protected override void OnMouseMove(MouseEventArgs e) { Mouse = e.Location; Invalidate(); base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { Mouse = new Point(-9999, -9999); Invalidate(); base.OnMouseLeave(e); }
    }

    class Seg : Ctl
    {
        readonly string[] items;
        int selected;
        public event Action Changed;
        public int Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public Seg(Theme t, string[] items, int selected) : base(t)
        {
            this.items = items;
            this.selected = selected;
            Height = Dpi.Px(36);
            Cursor = Cursors.Hand;
        }

        RectangleF Cell(int i)
        {
            float pad = Dpi.Px(3), w = (Width - 2 * pad) / items.Length;
            return new RectangleF(pad + i * w, pad, w, Height - 2 * pad);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Begin(e);
            RectangleF all = Draw.Inset(ClientRectangle);
            Draw.Fill(g, T.Surface, all, all.Height / 2);
            Draw.Stroke(g, T.Border, all, all.Height / 2, 1);
            for (int i = 0; i < items.Length; i++)
            {
                RectangleF r = Cell(i);
                bool on = i == selected;
                if (on) Draw.Fill(g, T.Accent, r, r.Height / 2);
                else if (r.Contains(Mouse)) Draw.Fill(g, T.Hover, r, r.Height / 2);
                TextRenderer.DrawText(g, items[i], Font, Rectangle.Round(r), on ? T.OnAccent : T.Text, Draw.Center);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < items.Length; i++)
                if (i != selected && Cell(i).Contains(e.Location))
                {
                    selected = i;
                    Invalidate();
                    if (Changed != null) Changed();
                }
        }
    }

    class PillButton : Ctl
    {
        public string Glyph;

        public PillButton(Theme t, string text, string glyph) : base(t)
        {
            Text = text;
            Glyph = glyph;
            Height = Dpi.Px(36);
            Cursor = Cursors.Hand;
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Begin(e);
            RectangleF r = Draw.Inset(ClientRectangle);
            Draw.Fill(g, ClientRectangle.Contains(Mouse) ? T.Hover : T.Surface, r, r.Height / 2);
            Draw.Stroke(g, T.Border, r, r.Height / 2, 1);
            if (Glyph == null) { TextRenderer.DrawText(g, Text, Font, ClientRectangle, T.Text, Draw.Center); return; }
            Size ts = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int gw = Dpi.Px(22), x = (Width - ts.Width - gw) / 2;
            TextRenderer.DrawText(g, Glyph, T.Icons, new Rectangle(x, 0, gw, Height), T.Accent, Draw.Center);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gw, 0, Width - x - gw, Height), T.Text, Draw.Left);
        }
    }

    class Toggle : Ctl
    {
        public bool On;
        public event Action Changed;

        public Toggle(Theme t, bool on) : base(t)
        {
            On = on;
            Size = new Size(Dpi.Px(46), Dpi.Px(26));
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Begin(e);
            RectangleF r = Draw.Inset(ClientRectangle);
            Draw.Fill(g, On ? T.Accent : T.Surface, r, r.Height / 2);
            if (!On) Draw.Stroke(g, T.Border, r, r.Height / 2, 1);
            float d = r.Height - Dpi.Px(8), x = On ? r.Right - d - Dpi.Px(4) : r.X + Dpi.Px(4);
            using (var b = new SolidBrush(On ? T.OnAccent : T.Dim)) g.FillEllipse(b, x, r.Y + Dpi.Px(4), d, d);
        }

        protected override void OnMouseDown(MouseEventArgs e) { On = !On; Invalidate(); if (Changed != null) Changed(); }
    }

    class Stepper : Ctl
    {
        public int Value;
        public event Action Changed;

        public Stepper(Theme t, int value) : base(t)
        {
            Value = value;
            Size = new Size(Dpi.Px(150), Dpi.Px(36));
            Cursor = Cursors.Hand;
        }

        Rectangle Minus { get { return new Rectangle(0, 0, Height, Height); } }
        Rectangle Plus { get { return new Rectangle(Width - Height, 0, Height, Height); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Begin(e);
            RectangleF r = Draw.Inset(ClientRectangle);
            Draw.Fill(g, T.Surface, r, r.Height / 2);
            Draw.Stroke(g, T.Border, r, r.Height / 2, 1);
            foreach (Rectangle b in new[] { Minus, Plus })
            {
                if (b.Contains(Mouse)) { RectangleF h = b; h.Inflate(-Dpi.Px(4), -Dpi.Px(4)); Draw.Fill(g, T.Hover, h, h.Height / 2); }
            }
            TextRenderer.DrawText(g, "", T.Icons, Minus, T.Text, Draw.Center); // minus
            TextRenderer.DrawText(g, "", T.Icons, Plus, T.Text, Draw.Center);  // plus
            TextRenderer.DrawText(g, Value.ToString(), Font, ClientRectangle, T.Text, Draw.Center);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int step = Value >= 50 ? 10 : Value >= 20 ? 5 : 1;
            int v = Minus.Contains(e.Location) ? Value - step : Plus.Contains(e.Location) ? Value + step : Value;
            v = Math.Max(1, Math.Min(1000, v));
            if (v == Value) return;
            Value = v;
            Invalidate();
            if (Changed != null) Changed();
        }
    }

    class Swatches : Ctl
    {
        static readonly Color[] Presets = {
            Color.FromArgb(255, 138, 196), Color.FromArgb(167, 139, 250), Color.FromArgb(96, 165, 250), Color.FromArgb(45, 212, 191),
            Color.FromArgb(52, 211, 153), Color.FromArgb(251, 191, 36), Color.FromArgb(248, 113, 113), Color.FromArgb(203, 213, 225) };
        public Color Value;
        public event Action Changed;

        public Swatches(Theme t, Color value) : base(t)
        {
            Value = value;
            Height = Dpi.Px(36);
            Cursor = Cursors.Hand;
        }

        Rectangle Slot(int i) { int s = Dpi.Px(36); return new Rectangle(i * s, 0, s, s); }
        static bool Same(Color a, Color b) { return a.R == b.R && a.G == b.G && a.B == b.B; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Begin(e);
            bool custom = true;
            for (int i = 0; i <= Presets.Length; i++)
            {
                Rectangle s = Slot(i);
                RectangleF c = new RectangleF(s.X + Dpi.Px(5), s.Y + Dpi.Px(5), s.Width - Dpi.Px(10), s.Height - Dpi.Px(10));
                bool isCustomSlot = i == Presets.Length;
                Color col = isCustomSlot ? Value : Presets[i];
                bool selected = isCustomSlot ? custom : Same(Value, col);
                if (selected && !isCustomSlot) custom = false;
                if (isCustomSlot && !custom)
                {
                    using (var pen = new Pen(T.Border, 1.5f)) g.DrawEllipse(pen, c);
                    TextRenderer.DrawText(g, "", T.Icons, s, T.Dim, Draw.Center);
                }
                else
                {
                    using (var b = new SolidBrush(col)) g.FillEllipse(b, c);
                }
                if (selected || s.Contains(Mouse))
                {
                    RectangleF ring = c;
                    ring.Inflate(Dpi.Px(3), Dpi.Px(3));
                    using (var pen = new Pen(selected ? T.Text : T.Border, 1.6f)) g.DrawEllipse(pen, ring);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < Presets.Length; i++)
                if (Slot(i).Contains(e.Location)) { Value = Presets[i]; Invalidate(); if (Changed != null) Changed(); return; }
            if (!Slot(Presets.Length).Contains(e.Location)) return;
            using (var dlg = new ColorDialog { Color = Value, FullOpen = true })
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) { Value = dlg.Color; Invalidate(); if (Changed != null) Changed(); }
        }
    }

    // Rounded text box: a borderless TextBox inside a painted frame.
    class Field : Panel
    {
        public readonly TextBox Box;
        readonly Theme T;

        public Field(Theme t, string text, bool multiline, bool password)
        {
            T = t;
            DoubleBuffered = true;
            BackColor = t.Bg;
            Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = t.Surface, ForeColor = t.Text, Font = t.Body, Multiline = multiline, UseSystemPasswordChar = password, Text = text ?? "" };
            Height = multiline ? Dpi.Px(88) : Dpi.Px(38);
            int top = multiline ? Dpi.Px(10) : Math.Max(0, (Height - Box.PreferredHeight) / 2);
            Padding = new Padding(Dpi.Px(14), top, Dpi.Px(14), Dpi.Px(8));
            Box.Dock = DockStyle.Fill;
            Controls.Add(Box);
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Click += delegate { Box.Focus(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = Draw.Inset(ClientRectangle);
            Draw.Fill(e.Graphics, T.Surface, r, Dpi.Px(10));
            Draw.Stroke(e.Graphics, Box.Focused ? T.Accent : T.Border, r, Dpi.Px(10), Box.Focused ? 1.6f : 1f);
        }
    }

    class Player
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr callback);
        public string Current;
        public bool Paused;

        static string Q(string cmd)
        {
            var sb = new StringBuilder(128);
            mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero);
            return sb.ToString();
        }

        static int Num(string cmd) { int v; int.TryParse(Q(cmd), out v); return v; }

        public void Toggle(string wav)
        {
            if (Current == wav) { Q(Paused ? "play clip" : "pause clip"); Paused = !Paused; return; }
            Stop();
            var sb = new StringBuilder(128);
            if (mciSendString("open \"" + wav + "\" type waveaudio alias clip", sb, sb.Capacity, IntPtr.Zero) != 0) return;
            Q("set clip time format milliseconds");
            Q("play clip");
            Current = wav;
            Paused = false;
        }

        public void Seek(double fraction)
        {
            if (Current == null) return;
            Q("play clip from " + (int)(Length * Math.Max(0, Math.Min(1, fraction))));
            Paused = false;
        }

        public int Position { get { return Current == null ? 0 : Num("status clip position"); } }
        public int Length { get { return Current == null ? 0 : Num("status clip length"); } }
        public bool Finished { get { return Current != null && !Paused && Q("status clip mode") == "stopped"; } }

        public void Stop()
        {
            if (Current == null) return;
            Q("stop clip");
            Q("close clip");
            Current = null;
            Paused = false;
        }
    }

    class HistoryView : Ctl
    {
        class Entry { public string Wav, Text; public DateTime When; public double Seconds; public bool Busy; public string Error; }
        class Card { public Entry E; public Rectangle R, Play, Copy, Retry, Del, Bar, Body; }

        readonly App app;
        readonly Player player = new Player();
        readonly Timer tick = new Timer { Interval = 50 };
        List<Entry> entries = new List<Entry>();
        readonly List<Card> cards = new List<Card>();
        int scroll, contentHeight;
        string copiedWav;
        DateTime copiedAt;

        int HeaderH { get { return Dpi.Px(44); } }

        public HistoryView(Theme t, App app) : base(t)
        {
            this.app = app;
            tick.Tick += delegate
            {
                if (player.Finished) player.Stop();
                if (player.Current == null && (DateTime.UtcNow - copiedAt).TotalSeconds > 1.5) tick.Stop();
                Invalidate();
            };
            Reload();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { player.Stop(); tick.Dispose(); }
            base.Dispose(disposing);
        }

        public void Reload()
        {
            var busy = new HashSet<string>();
            foreach (Entry e in entries) if (e.Busy) busy.Add(e.Wav);
            entries = new List<Entry>();
            foreach (FileInfo f in App.Recordings())
            {
                string txt = Path.ChangeExtension(f.FullName, ".txt");
                DateTime when;
                if (!DateTime.TryParseExact(f.Name.Substring(4, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out when)) when = f.LastWriteTime;
                entries.Add(new Entry
                {
                    Wav = f.FullName,
                    Text = File.Exists(txt) ? File.ReadAllText(txt, Encoding.UTF8) : "",
                    When = when,
                    Seconds = Math.Max(0, f.Length - 44) / 32000.0,
                    Busy = busy.Contains(f.FullName),
                });
            }
            Relayout();
            Invalidate();
        }

        void Relayout()
        {
            cards.Clear();
            int pad = Dpi.Px(20), inner = Dpi.Px(16), y = HeaderH;
            int w = Math.Max(Dpi.Px(260), Width - pad * 2);
            int play = Dpi.Px(40), icon = Dpi.Px(32);
            int textX = pad + inner + play + Dpi.Px(14);
            int textW = Math.Max(Dpi.Px(80), pad + w - inner - textX);
            foreach (Entry e in entries)
            {
                string shown = BodyText(e);
                int textH = TextRenderer.MeasureText(shown, Font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl).Height;
                int headH = Dpi.Px(32);
                int h = inner + headH + Dpi.Px(4) + textH + inner + Dpi.Px(8);
                var c = new Card { E = e, R = new Rectangle(pad, y, w, h) };
                c.Play = new Rectangle(pad + inner, y + inner, play, play);
                c.Del = new Rectangle(pad + w - inner - icon + Dpi.Px(6), y + Dpi.Px(12), icon, icon);
                c.Retry = new Rectangle(c.Del.X - icon, c.Del.Y, icon, icon);
                c.Copy = new Rectangle(c.Retry.X - icon, c.Del.Y, icon, icon);
                c.Body = new Rectangle(textX, y + inner + headH + Dpi.Px(4), textW, textH);
                c.Bar = new Rectangle(textX, y + h - inner - Dpi.Px(4), pad + w - inner - textX, Dpi.Px(4));
                cards.Add(c);
                y += h + Dpi.Px(10);
            }
            contentHeight = y + Dpi.Px(10);
            scroll = Math.Max(0, Math.Min(scroll, contentHeight - Height));
        }

        string BodyText(Entry e)
        {
            if (e.Busy) return L.T("Расшифровка…", "Transcribing…");
            if (e.Error != null) return e.Error;
            return e.Text.Length > 0 ? e.Text : L.T("Нет текста. Нажмите ↻, чтобы расшифровать.", "No text. Press ↻ to transcribe.");
        }

        protected override void OnResize(EventArgs e) { Relayout(); base.OnResize(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(scroll - e.Delta * Dpi.Px(60) / 120, contentHeight - Height));
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); } // so the wheel reaches us

        Rectangle FolderRect { get { return new Rectangle(Width - Dpi.Px(20) - Dpi.Px(150), Dpi.Px(6), Dpi.Px(150), Dpi.Px(32)); } }

        static string Clock(double seconds) { int s = (int)Math.Round(seconds); return (s / 60) + ":" + (s % 60).ToString("00"); }

        string When(DateTime d)
        {
            var culture = new CultureInfo(L.Lang == "en" ? "en-US" : "ru-RU");
            if (d.Date == DateTime.Today) return L.T("Сегодня, ", "Today, ") + d.ToString("HH:mm", culture);
            if (d.Date == DateTime.Today.AddDays(-1)) return L.T("Вчера, ", "Yesterday, ") + d.ToString("HH:mm", culture);
            return d.ToString("d MMM, HH:mm", culture);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            Graphics g = Begin(pe);
            int pad = Dpi.Px(20);
            string count = entries.Count == 0 ? "" : L.T("Записей: ", "Recordings: ") + entries.Count + L.T(" · хранятся последние ", " · keeping the last ") + app.Settings.KeepRecordings;

            if (entries.Count == 0)
            {
                Rectangle mid = new Rectangle(0, Height / 2 - Dpi.Px(70), Width, Dpi.Px(40));
                using (var f = new Font(T.Icons.FontFamily, 22f)) TextRenderer.DrawText(g, "", f, mid, T.Dim, Draw.Center); // microphone
                TextRenderer.DrawText(g, L.T("Записей пока нет", "No recordings yet"), T.Title, new Rectangle(0, mid.Bottom + Dpi.Px(8), Width, Dpi.Px(34)), T.Text, Draw.Center);
                TextRenderer.DrawText(g, L.T("Нажмите ", "Press ") + Hotkey.Parse(app.Settings.Hotkey) + L.T(" и говорите", " and speak"), Font, new Rectangle(0, mid.Bottom + Dpi.Px(44), Width, Dpi.Px(24)), T.Dim, Draw.Center);
            }

            foreach (Card c in cards)
            {
                Rectangle r = c.R;
                r.Offset(0, -scroll);
                if (r.Bottom < HeaderH || r.Top > Height) continue;
                g.SetClip(new Rectangle(0, HeaderH, Width, Height - HeaderH));
                DrawCard(g, c, -scroll);
                g.ResetClip();
            }

            // Fixed header over the scrolling list.
            using (var b = new SolidBrush(T.Bg)) g.FillRectangle(b, 0, 0, Width, HeaderH);
            TextRenderer.DrawText(g, count, T.Small, new Rectangle(pad, 0, Width - pad * 2 - Dpi.Px(160), HeaderH), T.Dim, Draw.Left);
            Rectangle fr = FolderRect;
            if (fr.Contains(Mouse)) Draw.Fill(g, T.Hover, fr, fr.Height / 2f);
            TextRenderer.DrawText(g, "", T.Icons, new Rectangle(fr.X + Dpi.Px(8), fr.Y, Dpi.Px(24), fr.Height), T.Accent, Draw.Center);
            TextRenderer.DrawText(g, L.T("Открыть папку", "Open folder"), T.Small, new Rectangle(fr.X + Dpi.Px(34), fr.Y, fr.Width - Dpi.Px(38), fr.Height), T.Text, Draw.Left);

            if (contentHeight > Height)
            {
                float trackH = Height - HeaderH - Dpi.Px(8);
                float thumbH = Math.Max(Dpi.Px(30), trackH * (Height - HeaderH) / (contentHeight - HeaderH));
                float thumbY = HeaderH + Dpi.Px(4) + (trackH - thumbH) * scroll / Math.Max(1, contentHeight - Height);
                Draw.Fill(g, T.Border, new RectangleF(Width - Dpi.Px(7), thumbY, Dpi.Px(4), thumbH), Dpi.Px(2));
            }
        }

        void DrawCard(Graphics g, Card c, int dy)
        {
            Rectangle r = c.R; r.Offset(0, dy);
            Point m = new Point(Mouse.X, Mouse.Y - dy);
            bool hover = c.R.Contains(m);
            Draw.Fill(g, hover ? T.Hover : T.Surface, r, Dpi.Px(14));
            if (!T.Dark) Draw.Stroke(g, T.Border, Draw.Inset(r), Dpi.Px(14), 1);

            bool current = player.Current == c.E.Wav;
            bool playing = current && !player.Paused;
            Rectangle play = c.Play; play.Offset(0, dy);
            using (var b = new SolidBrush(current || c.Play.Contains(m) ? T.Accent : Color.FromArgb(T.Dark ? 48 : 36, T.Accent))) g.FillEllipse(b, play);
            TextRenderer.DrawText(g, playing ? "" : "", T.Icons, play, current || c.Play.Contains(m) ? T.OnAccent : T.Accent, Draw.Center);

            string time = current ? Clock(player.Position / 1000.0) + " / " + Clock(c.E.Seconds) : Clock(c.E.Seconds);
            Rectangle head = new Rectangle(c.Body.X, r.Y + Dpi.Px(16), c.Copy.X - c.Body.X - Dpi.Px(8), Dpi.Px(32));
            TextRenderer.DrawText(g, When(c.E.When) + "  ·  " + time, T.Small, head, T.Dim, Draw.Left);

            bool copied = copiedWav == c.E.Wav && (DateTime.UtcNow - copiedAt).TotalSeconds < 1.5;
            IconButton(g, c.Copy, dy, m, copied ? "" : "", copied ? T.Accent : (Color?)null, c.E.Text.Length > 0);
            IconButton(g, c.Retry, dy, m, "", null, !c.E.Busy);
            IconButton(g, c.Del, dy, m, "", null, true);

            Rectangle body = c.Body; body.Offset(0, dy);
            bool placeholder = c.E.Busy || c.E.Error != null || c.E.Text.Length == 0;
            TextRenderer.DrawText(g, BodyText(c.E), Font, body, placeholder ? T.Dim : T.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);

            Rectangle bar = c.Bar; bar.Offset(0, dy);
            if (current)
            {
                Draw.Fill(g, T.Border, bar, bar.Height / 2f);
                float frac = player.Length > 0 ? Math.Min(1f, player.Position / (float)player.Length) : 0f;
                Draw.Fill(g, T.Accent, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * frac), bar.Height), bar.Height / 2f);
            }
        }

        void IconButton(Graphics g, Rectangle rect, int dy, Point m, string glyph, Color? color, bool enabled)
        {
            Rectangle r = rect; r.Offset(0, dy);
            bool hover = enabled && rect.Contains(m);
            if (hover) using (var b = new SolidBrush(T.Dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0))) g.FillEllipse(b, r);
            Color c = color ?? (!enabled ? Color.FromArgb(90, T.Dim) : hover ? T.Text : T.Dim);
            TextRenderer.DrawText(g, glyph, T.Icons, r, c, Draw.Center);
        }

        Card At(Point p)
        {
            var q = new Point(p.X, p.Y + scroll);
            if (p.Y < HeaderH) return null;
            foreach (Card c in cards) if (c.R.Contains(q)) return c;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Card c = At(e.Location);
            var q = new Point(e.X, e.Y + scroll);
            bool hand = FolderRect.Contains(e.Location) || (c != null && (c.Play.Contains(q) || c.Copy.Contains(q) || c.Retry.Contains(q) || c.Del.Contains(q) || (player.Current == c.E.Wav && InflatedBar(c).Contains(q))));
            Cursor = hand ? Cursors.Hand : Cursors.Default;
        }

        static Rectangle InflatedBar(Card c) { Rectangle b = c.Bar; b.Inflate(0, Dpi.Px(6)); return b; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (FolderRect.Contains(e.Location)) { App.OpenFolder(); return; }
            Card c = At(e.Location);
            if (c == null) return;
            var q = new Point(e.X, e.Y + scroll);
            if (c.Play.Contains(q)) { player.Toggle(c.E.Wav); tick.Start(); }
            else if (player.Current == c.E.Wav && InflatedBar(c).Contains(q)) player.Seek((q.X - c.Bar.X) / (double)c.Bar.Width);
            else if (c.Copy.Contains(q) && c.E.Text.Length > 0)
            {
                Clipboard.SetDataObject(c.E.Text, true, 10, 50);
                copiedWav = c.E.Wav;
                copiedAt = DateTime.UtcNow;
                tick.Start();
            }
            else if (c.Retry.Contains(q) && !c.E.Busy)
            {
                Entry entry = c.E;
                entry.Busy = true;
                entry.Error = null;
                Relayout();
                app.Retranscribe(entry.Wav, error =>
                {
                    entry.Busy = false;
                    Reload();
                    if (error == null) return;
                    foreach (Entry x in entries) if (x.Wav == entry.Wav) x.Error = error;
                    Relayout();
                    Invalidate();
                });
            }
            else if (c.Del.Contains(q))
            {
                if (player.Current == c.E.Wav) player.Stop();
                try
                {
                    // To the Recycle Bin, so a mistaken click can be undone.
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(c.E.Wav, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    string txt = Path.ChangeExtension(c.E.Wav, ".txt");
                    if (File.Exists(txt)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(txt, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex) { Log.Write("delete: " + ex.Message); }
                Reload();
            }
            Invalidate();
        }
    }

    class SettingsPage : Panel
    {
        readonly App app;
        readonly MainWindow window;
        readonly Theme T;
        readonly List<Control[]> rows = new List<Control[]>(); // { caption, control, fullWidth? }
        readonly List<bool> full = new List<bool>();
        readonly List<int> widths = new List<int>(); // wanted widths; the actual one may be narrower
        readonly Timer saveLater = new Timer { Interval = 600 };
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        public SettingsPage(Theme t, App app, MainWindow window)
        {
            T = t;
            this.app = app;
            this.window = window;
            BackColor = t.Bg;
            AutoScroll = true;
            DoubleBuffered = true;
            Settings s = app.Settings;
            saveLater.Tick += delegate { saveLater.Stop(); app.ApplySettings(); };

            var hotkeyBtn = new PillButton(t, Hotkey.Parse(s.Hotkey).ToString(), ""); // keyboard
            hotkeyBtn.Click += delegate
            {
                hotkeyBtn.Text = L.T("Нажмите сочетание… (Esc — отмена)", "Press a combination… (Esc to cancel)");
                app.Capture = h =>
                {
                    if (!(h.Key == Keys.Escape && !h.Ctrl && !h.Alt && !h.Shift && !h.Win)) { s.Hotkey = h.ToString(); app.ApplySettings(); }
                    if (!hotkeyBtn.IsDisposed) hotkeyBtn.Text = Hotkey.Parse(s.Hotkey).ToString();
                };
            };
            Add(L.T("Горячая клавиша", "Hotkey"), null, hotkeyBtn, false, Dpi.Px(300));

            var mode = new Seg(t, new[] { L.T("Удержание или нажатие", "Hold or toggle"), L.T("Нажатие", "Toggle"), L.T("Удержание", "Hold") }, Math.Max(0, Array.IndexOf(App.Modes, s.Mode)));
            mode.Changed += delegate { s.Mode = App.Modes[mode.Selected]; app.ApplySettings(); };
            Add(L.T("Режим", "Mode"), L.T("Короткое нажатие включает и выключает запись. Если держать клавишу, запись идёт, пока держите.", "A short press starts and stops recording. Holding the key records while you hold it."), mode, true, 0);

            var lang = new Seg(t, new[] { "Русский", "English" }, s.Language == "en" ? 1 : 0);
            lang.Changed += delegate { s.Language = lang.Selected == 1 ? "en" : "ru"; Rebuild(); };
            Add(L.T("Язык", "Language"), null, lang, false, Dpi.Px(300));

            var theme = new Seg(t, new[] { L.T("Тёмная", "Dark"), L.T("Светлая", "Light") }, s.Theme == "light" ? 1 : 0);
            theme.Changed += delegate { s.Theme = theme.Selected == 1 ? "light" : "dark"; Rebuild(); };
            Add(L.T("Тема", "Theme"), null, theme, false, Dpi.Px(300));

            var colors = new Swatches(t, s.AccentColor);
            colors.Changed += delegate { Color v = colors.Value; s.Accent = string.Format("#{0:X2}{1:X2}{2:X2}", v.R, v.G, v.B); Rebuild(); };
            Add(L.T("Цвет", "Colour"), null, colors, false, Dpi.Px(36) * 9);

            var key = new Field(t, s.ApiKey, false, true);
            key.Box.TextChanged += delegate { s.ApiKey = key.Box.Text.Trim(); Later(); };
            Add(L.T("Ключ Gemini API", "Gemini API key"), L.T("Если пусто, берётся переменная GEMINI_API_KEY. Ключ хранится только на этом компьютере.", "If empty, the GEMINI_API_KEY variable is used. The key stays on this computer."), key, true, 0);

            var model = new Field(t, s.Model, false, false);
            model.Box.TextChanged += delegate { s.Model = model.Box.Text.Trim(); Later(); };
            Add(L.T("Модель Gemini", "Gemini model"), L.T("Если не ответит, пробуются gemini-3.5-flash, gemini-flash-lite-latest и gemini-flash-latest.", "If it does not answer, gemini-3.5-flash, gemini-flash-lite-latest and gemini-flash-latest are tried."), model, true, 0);

            var hint = new Field(t, s.Hint, true, false);
            hint.Box.TextChanged += delegate { s.Hint = hint.Box.Text.Trim(); Later(); };
            Add(L.T("Подсказка для Gemini", "Hint for Gemini"), L.T("На каких языках вы говорите, какие имена и термины встречаются.", "Which languages you speak, which names and terms come up."), hint, true, 0);

            var keep = new Stepper(t, Math.Max(1, s.KeepRecordings));
            keep.Changed += delegate { s.KeepRecordings = keep.Value; Later(); };
            Add(L.T("Хранить записей", "Keep recordings"), L.T("Более старые записи удаляются сами.", "Older recordings are deleted automatically."), keep, false, Dpi.Px(150));

            var autostart = new Toggle(t, Autostart);
            autostart.Changed += delegate { Autostart = autostart.On; };
            Add(L.T("Запускать вместе с Windows", "Start with Windows"), null, autostart, false, Dpi.Px(46));

            if (t.Dark) HandleCreated += delegate { SetWindowTheme(Handle, "DarkMode_Explorer", null); }; // dark scrollbar
            LayoutRows();
        }

        static bool Autostart
        {
            get { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("AudioSpeech") != null; }
            set
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("AudioSpeech", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("AudioSpeech", false);
                }
            }
        }

        void Later() { saveLater.Stop(); saveLater.Start(); }

        void Rebuild()
        {
            saveLater.Stop();
            app.ApplySettings();
            BeginInvoke(new Action(window.Rebuild)); // not inside the click handler of a control that is about to be disposed
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && saveLater.Enabled) { saveLater.Stop(); app.ApplySettings(); }
            if (disposing) saveLater.Dispose();
            base.Dispose(disposing);
        }

        void Add(string title, string desc, Control c, bool fullWidth, int width)
        {
            var cap = new Caption(T, title, desc);
            Controls.Add(cap);
            Controls.Add(c);
            rows.Add(new Control[] { cap, c });
            full.Add(fullWidth);
            widths.Add(width);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutRows(); }

        void LayoutRows()
        {
            int pad = Dpi.Px(24), w = Math.Max(Dpi.Px(240), ClientSize.Width - pad * 2), y = Dpi.Px(8) + AutoScrollPosition.Y;
            for (int i = 0; i < rows.Count; i++)
            {
                var cap = (Caption)rows[i][0];
                Control c = rows[i][1];
                cap.SetBounds(pad, y, w, cap.Measure(w));
                y += cap.Height + Dpi.Px(8);
                c.SetBounds(pad, y, full[i] ? w : Math.Min(widths[i], w), c.Height);
                y += c.Height + Dpi.Px(22);
            }
            AutoScrollMinSize = new Size(0, y - AutoScrollPosition.Y);
        }
    }

    class Caption : Ctl
    {
        readonly string title, desc;

        public Caption(Theme t, string title, string desc) : base(t) { this.title = title; this.desc = desc; }

        public int Measure(int width)
        {
            int h = Dpi.Px(22);
            if (desc != null) h += TextRenderer.MeasureText(desc, T.Small, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + Dpi.Px(2);
            return h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Begin(e);
            using (var f = new Font(T.Body, FontStyle.Bold)) TextRenderer.DrawText(g, title, f, new Rectangle(0, 0, Width, Dpi.Px(22)), T.Text, Draw.Left);
            if (desc != null) TextRenderer.DrawText(g, desc, T.Small, new Rectangle(0, Dpi.Px(22), Width, Height - Dpi.Px(22)), T.Dim, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }

    class Header : Ctl
    {
        public Header(Theme t) : base(t) { Height = Dpi.Px(68); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = Begin(e);
            int x = Dpi.Px(22), cy = Height / 2;
            using (var halo = new SolidBrush(Color.FromArgb(60, T.Accent))) g.FillEllipse(halo, x, cy - Dpi.Px(9), Dpi.Px(18), Dpi.Px(18));
            using (var dot = new SolidBrush(T.Accent)) g.FillEllipse(dot, x + Dpi.Px(4), cy - Dpi.Px(5), Dpi.Px(10), Dpi.Px(10));
            TextRenderer.DrawText(g, "AudioSpeech", T.Title, new Rectangle(x + Dpi.Px(28), 0, Width / 2, Height), T.Text, Draw.Left);
        }
    }

    class MainWindow : Form
    {
        readonly App app;
        int tab;
        Theme theme;
        HistoryView history;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MainWindow(App app, int tab)
        {
            this.app = app;
            this.tab = tab;
            Text = "AudioSpeech";
            Icon = app.TrayIcon;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Dpi.Px(620), Dpi.Px(700));
            MinimumSize = new Size(Dpi.Px(500), Dpi.Px(440));
            Rebuild();
        }

        public void ShowTab(int t) { if (t != tab) { tab = t; Rebuild(); } }

        public void RefreshHistory() { if (history != null && !history.IsDisposed) history.Reload(); }

        public void Rebuild()
        {
            SuspendLayout();
            var old = new List<Control>();
            foreach (Control c in Controls) old.Add(c);
            Controls.Clear();
            foreach (Control c in old) c.Dispose();
            history = null;

            theme = Theme.From(app.Settings);
            BackColor = theme.Bg;
            Font = theme.Body;
            Icon = app.TrayIcon;

            var header = new Header(theme) { Dock = DockStyle.Top };
            var tabs = new Seg(theme, new[] { L.T("История", "History"), L.T("Настройки", "Settings") }, tab) { Width = Dpi.Px(250), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            tabs.Location = new Point(ClientSize.Width - tabs.Width - Dpi.Px(20), (header.Height - tabs.Height) / 2);
            header.Controls.Add(tabs);
            tabs.Changed += delegate { tab = tabs.Selected; BeginInvoke(new Action(Rebuild)); };

            Control content;
            if (tab == 0) content = history = new HistoryView(theme, app);
            else content = new SettingsPage(theme, app, this);
            content.Dock = DockStyle.Fill;
            Controls.Add(content);
            Controls.Add(header); // added last, so it docks first (top)
            ResumeLayout();
            header.Width = ClientSize.Width;
            tabs.Left = ClientSize.Width - tabs.Width - Dpi.Px(20);
            ApplyTitleBar();
        }

        // Dark/light title bar in the panel colour (Windows 10 2004+ / Windows 11; ignored elsewhere).
        void ApplyTitleBar()
        {
            int dark = theme.Dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            int caption = theme.Bg.R | (theme.Bg.G << 8) | (theme.Bg.B << 16);
            DwmSetWindowAttribute(Handle, 35, ref caption, 4);
        }
    }
}
