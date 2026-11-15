// AudioSpeech main window: recordings history with a player, and settings. Everything is custom-drawn
// to match the floating panel; colours come from the theme and accent colour. Hover and press effects are
// animated: colours fade, selections slide, and a press leaves a fading ripple.
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
                t.Bg = Color.FromArgb(24, 24, 27); t.Surface = Color.FromArgb(35, 35, 40); t.Hover = Color.FromArgb(47, 47, 54);
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

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    // Base for custom-drawn controls: double-buffered, mouse tracking, eased animations and press ripples.
    class Ctl : Control
    {
        protected readonly Theme T;
        protected Point Mouse = new Point(-9999, -9999);
        readonly Dictionary<int, float> values = new Dictionary<int, float>();
        readonly Timer frame = new Timer { Interval = 15 };
        bool moving;

        class Ripple { public PointF At; public RectangleF Area; public float Radius; public DateTime Start; public Color Color; }
        readonly List<Ripple> ripples = new List<Ripple>();

        public Ctl(Theme t)
        {
            T = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg;
            Font = t.Body;
            frame.Tick += delegate { Invalidate(); };
        }

        protected override void Dispose(bool disposing) { if (disposing) frame.Dispose(); base.Dispose(disposing); }

        /// <summary>Moves value <paramref name="key"/> a step toward <paramref name="goal"/> (ease-out) and keeps frames coming until it arrives.</summary>
        protected float Ease(int key, float goal, float speed = 0.22f)
        {
            float v;
            if (!values.TryGetValue(key, out v)) v = goal;
            if (Math.Abs(goal - v) < 0.004f) v = goal;
            else { v += (goal - v) * speed; moving = true; }
            values[key] = v;
            return v;
        }

        protected float Ease(int key, bool on) { return Ease(key, on ? 1f : 0f); }

        protected void Press(PointF at, RectangleF area, float radius, Color color)
        {
            ripples.Add(new Ripple { At = at, Area = area, Radius = radius, Start = DateTime.UtcNow, Color = color });
            frame.Start();
        }

        protected virtual Color RippleColor { get { return T.Dark ? Color.White : Color.Black; } }

        protected sealed override void OnPaint(PaintEventArgs e)
        {
            moving = false;
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Render(g);
            for (int i = ripples.Count - 1; i >= 0; i--)
            {
                Ripple r = ripples[i];
                float t = (float)(DateTime.UtcNow - r.Start).TotalMilliseconds / 450f;
                if (t >= 1) { ripples.RemoveAt(i); continue; }
                float ease = 1 - (1 - t) * (1 - t);
                float reach = (float)Math.Sqrt(r.Area.Width * r.Area.Width + r.Area.Height * r.Area.Height);
                float rad = reach * (0.2f + 0.8f * ease);
                using (GraphicsPath clip = Draw.Round(r.Area, r.Radius))
                using (var b = new SolidBrush(Color.FromArgb((int)(55 * (1 - t)), r.Color)))
                {
                    g.SetClip(clip);
                    g.FillEllipse(b, r.At.X - rad, r.At.Y - rad, rad * 2, rad * 2);
                    g.ResetClip();
                }
                moving = true;
            }
            if (moving) frame.Start(); else frame.Stop();
        }

        protected virtual void Render(Graphics g) { }

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

        RectangleF Cell(float i)
        {
            float pad = Dpi.Px(3), w = (Width - 2 * pad) / items.Length;
            return new RectangleF(pad + i * w, pad, w, Height - 2 * pad);
        }

        protected override void Render(Graphics g)
        {
            RectangleF all = Draw.Inset(ClientRectangle);
            Draw.Fill(g, T.Surface, all, all.Height / 2);
            Draw.Stroke(g, T.Border, all, all.Height / 2, 1);
            for (int i = 0; i < items.Length; i++)
            {
                float h = Ease(i, i != selected && Cell(i).Contains(Mouse));
                if (h > 0) { RectangleF r = Cell(i); Draw.Fill(g, Color.FromArgb((int)(255 * h), T.Hover), r, r.Height / 2); }
            }
            float pos = Ease(-1, selected, 0.25f); // the selection slides between options
            RectangleF pill = Cell(pos);
            Draw.Fill(g, T.Accent, pill, pill.Height / 2);
            for (int i = 0; i < items.Length; i++)
            {
                float cover = Math.Max(0, 1 - Math.Abs(pos - i));
                TextRenderer.DrawText(g, items[i], Font, Rectangle.Round(Cell(i)), Draw.Mix(T.Text, T.OnAccent, cover), Draw.Center);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < items.Length; i++)
                if (Cell(i).Contains(e.Location))
                {
                    RectangleF r = Cell(i);
                    Press(e.Location, r, r.Height / 2, RippleColor);
                    if (i == selected) return;
                    selected = i;
                    Invalidate();
                    if (Changed != null) Changed();
                    return;
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

        protected override void Render(Graphics g)
        {
            RectangleF r = Draw.Inset(ClientRectangle);
            float h = Ease(0, ClientRectangle.Contains(Mouse));
            Draw.Fill(g, Draw.Mix(T.Surface, T.Hover, h), r, r.Height / 2);
            Draw.Stroke(g, Draw.Mix(T.Border, T.Accent, h * 0.6f), r, r.Height / 2, 1);
            if (Glyph == null) { TextRenderer.DrawText(g, Text, Font, ClientRectangle, T.Text, Draw.Center); return; }
            Size ts = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int gw = Dpi.Px(24), x = (Width - ts.Width - gw) / 2;
            TextRenderer.DrawText(g, Glyph, T.Icons, new Rectangle(x, 0, gw, Height), T.Accent, Draw.Center);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gw, 0, Width - x - gw, Height), T.Text, Draw.Left);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Press(e.Location, Draw.Inset(ClientRectangle), Height / 2f, T.Accent); base.OnMouseDown(e); }
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

        protected override void Render(Graphics g)
        {
            RectangleF r = Draw.Inset(ClientRectangle);
            float k = Ease(0, On), h = Ease(1, ClientRectangle.Contains(Mouse));
            Draw.Fill(g, Draw.Mix(Draw.Mix(T.Surface, T.Hover, h), T.Accent, k), r, r.Height / 2);
            Draw.Stroke(g, Color.FromArgb((int)(255 * (1 - k)), T.Border), r, r.Height / 2, 1);
            float baseD = r.Height - Dpi.Px(8), d = baseD + Dpi.Px(2) * h;
            float off = r.X + Dpi.Px(4), on = r.Right - baseD - Dpi.Px(4);
            float x = off + (on - off) * k - (d - baseD) / 2; // the knob slides, and grows a little on hover
            using (var b = new SolidBrush(Draw.Mix(T.Dim, T.OnAccent, k))) g.FillEllipse(b, x, r.Y + (r.Height - d) / 2, d, d);
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

        static RectangleF Knob(Rectangle b) { RectangleF h = b; h.Inflate(-Dpi.Px(4), -Dpi.Px(4)); return h; }

        protected override void Render(Graphics g)
        {
            RectangleF r = Draw.Inset(ClientRectangle);
            Draw.Fill(g, T.Surface, r, r.Height / 2);
            Draw.Stroke(g, T.Border, r, r.Height / 2, 1);
            Rectangle[] buttons = { Minus, Plus };
            for (int i = 0; i < 2; i++)
            {
                float h = Ease(i, buttons[i].Contains(Mouse));
                if (h > 0) { RectangleF k = Knob(buttons[i]); Draw.Fill(g, Color.FromArgb((int)(255 * h), T.Hover), k, k.Height / 2); }
            }
            TextRenderer.DrawText(g, "", T.Icons, Minus, T.Text, Draw.Center); // minus
            TextRenderer.DrawText(g, "", T.Icons, Plus, T.Text, Draw.Center);  // plus
            TextRenderer.DrawText(g, Value.ToString(), Font, ClientRectangle, T.Text, Draw.Center);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Rectangle hit = Minus.Contains(e.Location) ? Minus : Plus.Contains(e.Location) ? Plus : Rectangle.Empty;
            if (hit.IsEmpty) return;
            RectangleF k = Knob(hit);
            Press(e.Location, k, k.Height / 2, RippleColor);
            int step = Value >= 50 ? 10 : Value >= 20 ? 5 : 1;
            int v = Math.Max(1, Math.Min(1000, hit == Minus ? Value - step : Value + step));
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

        protected override void Render(Graphics g)
        {
            Color value = Value;
            int match = Array.FindIndex(Presets, c => Same(c, value));
            for (int i = 0; i <= Presets.Length; i++)
            {
                Rectangle s = Slot(i);
                bool customSlot = i == Presets.Length;
                bool selected = customSlot ? match < 0 : i == match;
                float h = Ease(i, s.Contains(Mouse)), sel = Ease(100 + i, selected);
                float grow = Dpi.Px(3) * h;
                RectangleF c = new RectangleF(s.X + Dpi.Px(6) - grow / 2, s.Y + Dpi.Px(6) - grow / 2, s.Width - Dpi.Px(12) + grow, s.Height - Dpi.Px(12) + grow);
                if (customSlot && match >= 0)
                {
                    using (var pen = new Pen(Draw.Mix(T.Border, T.Dim, h), 1.5f)) g.DrawEllipse(pen, c);
                    TextRenderer.DrawText(g, "", T.Icons, s, Draw.Mix(T.Dim, T.Text, h), Draw.Center);
                }
                else using (var b = new SolidBrush(customSlot ? value : Presets[i])) g.FillEllipse(b, c);
                if (sel > 0)
                {
                    RectangleF ring = c;
                    ring.Inflate(Dpi.Px(3), Dpi.Px(3));
                    using (var pen = new Pen(Color.FromArgb((int)(255 * sel), T.Text), 1.6f)) g.DrawEllipse(pen, ring);
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

    // A vertical list of options with radio dots (microphones).
    class Choices : Ctl
    {
        readonly string[] titles, subtitles;
        int selected;
        public event Action Changed;
        public int Selected { get { return selected; } }
        int RowH { get { return Dpi.Px(48); } }

        public Choices(Theme t, string[] titles, string[] subtitles, int selected) : base(t)
        {
            this.titles = titles;
            this.subtitles = subtitles;
            this.selected = selected;
            Height = RowH * titles.Length + Dpi.Px(8);
            Cursor = Cursors.Hand;
        }

        Rectangle Row(int i) { return new Rectangle(Dpi.Px(4), Dpi.Px(4) + i * RowH, Width - Dpi.Px(8), RowH); }

        protected override void Render(Graphics g)
        {
            RectangleF all = Draw.Inset(ClientRectangle);
            Draw.Fill(g, T.Surface, all, Dpi.Px(14));
            Draw.Stroke(g, T.Border, all, Dpi.Px(14), 1);
            for (int i = 0; i < titles.Length; i++)
            {
                Rectangle r = Row(i);
                float h = Ease(i, r.Contains(Mouse)), on = Ease(100 + i, i == selected);
                if (h > 0) Draw.Fill(g, Color.FromArgb((int)(255 * h), T.Hover), r, Dpi.Px(10));
                float cx = r.X + Dpi.Px(22), cy = r.Y + r.Height / 2f, outer = Dpi.Px(9);
                using (var pen = new Pen(Draw.Mix(T.Dim, T.Accent, on), 1.6f)) g.DrawEllipse(pen, cx - outer, cy - outer, outer * 2, outer * 2);
                float inner = Dpi.Px(5) * on; // the dot pops in
                if (inner > 0.3f) using (var b = new SolidBrush(T.Accent)) g.FillEllipse(b, cx - inner, cy - inner, inner * 2, inner * 2);
                int tx = r.X + Dpi.Px(44);
                if (subtitles[i] == null) TextRenderer.DrawText(g, titles[i], Font, new Rectangle(tx, r.Y, r.Right - tx - Dpi.Px(8), r.Height), T.Text, Draw.Left);
                else
                {
                    TextRenderer.DrawText(g, titles[i], Font, new Rectangle(tx, r.Y + Dpi.Px(5), r.Right - tx - Dpi.Px(8), r.Height / 2), T.Text, Draw.Left);
                    TextRenderer.DrawText(g, subtitles[i], T.Small, new Rectangle(tx, r.Y + r.Height / 2, r.Right - tx - Dpi.Px(8), r.Height / 2 - Dpi.Px(5)), T.Dim, Draw.Left);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < titles.Length; i++)
                if (Row(i).Contains(e.Location))
                {
                    Press(e.Location, Row(i), Dpi.Px(10), RippleColor);
                    if (i == selected) return;
                    selected = i;
                    Invalidate();
                    if (Changed != null) Changed();
                    return;
                }
        }
    }

    // Rounded text box: a borderless TextBox inside a painted frame whose border fades to the accent on focus.
    class Field : Panel
    {
        public readonly TextBox Box;
        readonly Theme T;
        float focus;
        readonly Timer frame = new Timer { Interval = 15 };

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
            frame.Tick += delegate
            {
                float goal = Box.Focused ? 1 : 0;
                focus += (goal - focus) * 0.25f;
                if (Math.Abs(goal - focus) < 0.01f) { focus = goal; frame.Stop(); }
                Invalidate();
            };
            Box.GotFocus += delegate { frame.Start(); };
            Box.LostFocus += delegate { frame.Start(); };
            Click += delegate { Box.Focus(); };
        }

        protected override void Dispose(bool disposing) { if (disposing) frame.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = Draw.Inset(ClientRectangle);
            Draw.Fill(e.Graphics, T.Surface, r, Dpi.Px(10));
            Draw.Stroke(e.Graphics, Draw.Mix(T.Border, T.Accent, focus), r, Dpi.Px(10), 1f + 0.6f * focus);
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
        int scrollGoal, contentHeight;
        float scroll;
        string copiedWav;
        DateTime copiedAt;

        int HeaderH { get { return Dpi.Px(44); } }
        int MaxScroll { get { return Math.Max(0, contentHeight - Height); } }

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
                int textH = TextRenderer.MeasureText(BodyText(e), Font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl).Height;
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
            scrollGoal = Math.Max(0, Math.Min(scrollGoal, MaxScroll));
            scroll = Math.Min(scroll, MaxScroll);
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
            scrollGoal = Math.Max(0, Math.Min(scrollGoal - e.Delta * Dpi.Px(90) / 120, MaxScroll));
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

        int Dy { get { return -(int)Math.Round(scroll); } }

        protected override void Render(Graphics g)
        {
            scroll = Ease(-1, scrollGoal, 0.3f); // smooth scrolling
            int pad = Dpi.Px(20);
            string count = entries.Count == 0 ? "" : L.T("Записей: ", "Recordings: ") + entries.Count + L.T(" · хранятся последние ", " · keeping the last ") + app.Settings.KeepRecordings;

            if (entries.Count == 0)
            {
                Rectangle mid = new Rectangle(0, Height / 2 - Dpi.Px(70), Width, Dpi.Px(40));
                using (var f = new Font(T.Icons.FontFamily, 22f)) TextRenderer.DrawText(g, "", f, mid, T.Dim, Draw.Center); // microphone
                TextRenderer.DrawText(g, L.T("Записей пока нет", "No recordings yet"), T.Title, new Rectangle(0, mid.Bottom + Dpi.Px(8), Width, Dpi.Px(34)), T.Text, Draw.Center);
                TextRenderer.DrawText(g, L.T("Нажмите ", "Press ") + Hotkey.Parse(app.Settings.Hotkey) + L.T(" и говорите", " and speak"), Font, new Rectangle(0, mid.Bottom + Dpi.Px(44), Width, Dpi.Px(24)), T.Dim, Draw.Center);
            }

            g.SetClip(new Rectangle(0, HeaderH, Width, Height - HeaderH));
            for (int i = 0; i < cards.Count; i++)
            {
                Rectangle r = cards[i].R;
                r.Offset(0, Dy);
                if (r.Bottom < HeaderH || r.Top > Height) continue;
                DrawCard(g, cards[i], i, Dy);
            }
            g.ResetClip();

            // Fixed header over the scrolling list.
            using (var b = new SolidBrush(T.Bg)) g.FillRectangle(b, 0, 0, Width, HeaderH);
            TextRenderer.DrawText(g, count, T.Small, new Rectangle(pad, 0, Width - pad * 2 - Dpi.Px(160), HeaderH), T.Dim, Draw.Left);
            Rectangle fr = FolderRect;
            float fh = Ease(-2, fr.Contains(Mouse));
            if (fh > 0) Draw.Fill(g, Color.FromArgb((int)(255 * fh), T.Hover), fr, fr.Height / 2f);
            TextRenderer.DrawText(g, "", T.Icons, new Rectangle(fr.X + Dpi.Px(8), fr.Y, Dpi.Px(24), fr.Height), T.Accent, Draw.Center);
            TextRenderer.DrawText(g, L.T("Открыть папку", "Open folder"), T.Small, new Rectangle(fr.X + Dpi.Px(34), fr.Y, fr.Width - Dpi.Px(38), fr.Height), T.Text, Draw.Left);

            if (contentHeight > Height)
            {
                float trackH = Height - HeaderH - Dpi.Px(8);
                float thumbH = Math.Max(Dpi.Px(30), trackH * (Height - HeaderH) / (contentHeight - HeaderH));
                float thumbY = HeaderH + Dpi.Px(4) + (trackH - thumbH) * scroll / Math.Max(1, MaxScroll);
                Draw.Fill(g, T.Border, new RectangleF(Width - Dpi.Px(7), thumbY, Dpi.Px(4), thumbH), Dpi.Px(2));
            }
        }

        void DrawCard(Graphics g, Card c, int index, int dy)
        {
            Rectangle r = c.R; r.Offset(0, dy);
            Point m = new Point(Mouse.X, Mouse.Y - dy);
            int key = index * 10;
            float hover = Ease(key, c.R.Contains(m) && Mouse.Y >= HeaderH);
            Draw.Fill(g, Draw.Mix(T.Surface, T.Hover, hover), r, Dpi.Px(14));
            if (!T.Dark) Draw.Stroke(g, T.Border, Draw.Inset(r), Dpi.Px(14), 1);

            bool current = player.Current == c.E.Wav;
            bool playing = current && !player.Paused;
            float ph = Ease(key + 1, current || c.Play.Contains(m));
            RectangleF play = c.Play; play.Offset(0, dy);
            play.Inflate(Dpi.Px(2) * ph, Dpi.Px(2) * ph); // the play button swells a little on hover
            using (var b = new SolidBrush(Draw.Mix(Color.FromArgb(T.Dark ? 48 : 36, T.Accent), T.Accent, ph))) g.FillEllipse(b, play);
            TextRenderer.DrawText(g, playing ? "" : "", T.Icons, Rectangle.Round(play), Draw.Mix(T.Accent, T.OnAccent, ph), Draw.Center);

            string time = current ? Clock(player.Position / 1000.0) + " / " + Clock(c.E.Seconds) : Clock(c.E.Seconds);
            Rectangle head = new Rectangle(c.Body.X, r.Y + Dpi.Px(16), c.Copy.X - c.Body.X - Dpi.Px(8), Dpi.Px(32));
            TextRenderer.DrawText(g, When(c.E.When) + "  ·  " + time, T.Small, head, T.Dim, Draw.Left);

            bool copied = copiedWav == c.E.Wav && (DateTime.UtcNow - copiedAt).TotalSeconds < 1.5;
            IconButton(g, c.Copy, key + 2, dy, m, copied ? "" : "", copied ? T.Accent : (Color?)null, c.E.Text.Length > 0, hover);
            IconButton(g, c.Retry, key + 3, dy, m, "", null, !c.E.Busy, hover);
            IconButton(g, c.Del, key + 4, dy, m, "", null, true, hover);

            Rectangle body = c.Body; body.Offset(0, dy);
            bool placeholder = c.E.Busy || c.E.Error != null || c.E.Text.Length == 0;
            TextRenderer.DrawText(g, BodyText(c.E), Font, body, placeholder ? T.Dim : T.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);

            Rectangle bar = c.Bar; bar.Offset(0, dy);
            float show = Ease(key + 5, current);
            if (show > 0)
            {
                Draw.Fill(g, Color.FromArgb((int)(255 * show), T.Border), bar, bar.Height / 2f);
                float frac = current && player.Length > 0 ? Math.Min(1f, player.Position / (float)player.Length) : 0f;
                Draw.Fill(g, Color.FromArgb((int)(255 * show), T.Accent), new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * frac), bar.Height), bar.Height / 2f);
            }
        }

        void IconButton(Graphics g, Rectangle rect, int key, int dy, Point m, string glyph, Color? color, bool enabled, float cardHover)
        {
            Rectangle r = rect; r.Offset(0, dy);
            float h = Ease(key, enabled && rect.Contains(m));
            if (h > 0) using (var b = new SolidBrush(Color.FromArgb((int)((T.Dark ? 28 : 18) * h), T.Dark ? Color.White : Color.Black))) g.FillEllipse(b, r);
            Color idle = Draw.Mix(Color.FromArgb(150, T.Dim), T.Dim, cardHover); // icons brighten when the card is hovered
            Color c = color ?? (!enabled ? Color.FromArgb(80, T.Dim) : Draw.Mix(idle, T.Text, h));
            TextRenderer.DrawText(g, glyph, T.Icons, r, c, Draw.Center);
        }

        Card At(Point p)
        {
            if (p.Y < HeaderH) return null;
            var q = new Point(p.X, p.Y - Dy);
            foreach (Card c in cards) if (c.R.Contains(q)) return c;
            return null;
        }

        static Rectangle InflatedBar(Card c) { Rectangle b = c.Bar; b.Inflate(0, Dpi.Px(6)); return b; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Card c = At(e.Location);
            var q = new Point(e.X, e.Y - Dy);
            bool hand = FolderRect.Contains(e.Location) || (c != null && (c.Play.Contains(q) || c.Copy.Contains(q) || c.Retry.Contains(q) || c.Del.Contains(q) || (player.Current == c.E.Wav && InflatedBar(c).Contains(q))));
            Cursor = hand ? Cursors.Hand : Cursors.Default;
        }

        void Ripple(Rectangle target, Point at, bool round, Color color)
        {
            Rectangle r = target; r.Offset(0, Dy);
            Press(at, r, round ? r.Height / 2f : Dpi.Px(14), color);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (FolderRect.Contains(e.Location)) { Press(e.Location, FolderRect, FolderRect.Height / 2f, T.Accent); App.OpenFolder(); return; }
            Card c = At(e.Location);
            if (c == null) return;
            var q = new Point(e.X, e.Y - Dy);
            if (c.Play.Contains(q)) { Ripple(c.Play, e.Location, true, Color.White); player.Toggle(c.E.Wav); tick.Start(); }
            else if (player.Current == c.E.Wav && InflatedBar(c).Contains(q)) player.Seek((q.X - c.Bar.X) / (double)c.Bar.Width);
            else if (c.Copy.Contains(q) && c.E.Text.Length > 0)
            {
                Ripple(c.Copy, e.Location, true, RippleColor);
                Clipboard.SetDataObject(c.E.Text, true, 10, 50);
                copiedWav = c.E.Wav;
                copiedAt = DateTime.UtcNow;
                tick.Start();
            }
            else if (c.Retry.Contains(q) && !c.E.Busy)
            {
                Ripple(c.Retry, e.Location, true, RippleColor);
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
            else Ripple(c.R, e.Location, false, RippleColor);
            Invalidate();
        }
    }

    class SettingsPage : Panel
    {
        readonly App app;
        readonly MainWindow window;
        readonly Theme T;
        readonly List<Control[]> rows = new List<Control[]>(); // { caption, control }
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

            List<string> devices = Recorder.Devices();
            var titles = new List<string> { L.T("Автоматически", "Automatic") };
            var subs = new List<string> { L.T("Микрофон Windows по умолчанию, а если он недоступен, первый рабочий", "The Windows default microphone, or the first one that works") };
            var keys = new List<string> { "" }; // what is stored: the waveIn name
            foreach (string d in devices) { titles.Add(Recorder.FullName(d)); subs.Add(null); keys.Add(d); }
            int micIndex = string.IsNullOrEmpty(s.Microphone) ? 0 : devices.IndexOf(s.Microphone) + 1;
            if (micIndex <= 0 && !string.IsNullOrEmpty(s.Microphone))
            {
                titles.Add(s.Microphone);
                keys.Add(s.Microphone);
                subs.Add(L.T("Сейчас не подключён, используется автоматический выбор", "Not connected now; automatic is used"));
                micIndex = titles.Count - 1;
            }
            var mics = new Choices(t, titles.ToArray(), subs.ToArray(), micIndex);
            mics.Changed += delegate { s.Microphone = keys[mics.Selected]; app.ApplySettings(); };
            Add(L.T("Микрофон", "Microphone"), null, mics, true, 0);

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
            // Let the selection animate, then rebuild (never inside the handler of a control that is about to be disposed).
            var t = new Timer { Interval = 180 };
            t.Tick += delegate { t.Dispose(); window.Rebuild(); };
            t.Start();
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

        protected override void Render(Graphics g)
        {
            using (var f = new Font(T.Body, FontStyle.Bold)) TextRenderer.DrawText(g, title, f, new Rectangle(0, 0, Width, Dpi.Px(22)), T.Text, Draw.Left);
            if (desc != null) TextRenderer.DrawText(g, desc, T.Small, new Rectangle(0, Dpi.Px(22), Width, Height - Dpi.Px(22)), T.Dim, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }

    class Header : Ctl
    {
        public Header(Theme t) : base(t) { Height = Dpi.Px(68); }

        protected override void Render(Graphics g)
        {
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
            // Let the selection slide before the page is swapped.
            tabs.Changed += delegate { tab = tabs.Selected; var t = new Timer { Interval = 180 }; t.Tick += delegate { t.Dispose(); Rebuild(); }; t.Start(); };

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

        // Dark/light title bar in the window colour (Windows 10 2004+ / Windows 11; ignored elsewhere).
        void ApplyTitleBar()
        {
            int dark = theme.Dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            int caption = theme.Bg.R | (theme.Bg.G << 8) | (theme.Bg.B << 16);
            DwmSetWindowAttribute(Handle, 35, ref caption, 4);
        }
    }
}
