using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MiniVpn
{
    public enum VState { Off, Connecting, On, Blocked, Stopping }

    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x10, 0x13, 0x18);
        public static readonly Color Panel = Color.FromArgb(0x19, 0x1D, 0x24);
        public static readonly Color PanelHi = Color.FromArgb(0x24, 0x2A, 0x33);
        public static readonly Color Border = Color.FromArgb(0x2B, 0x31, 0x3B);
        public static readonly Color Text = Color.FromArgb(0xEC, 0xEE, 0xF1);
        public static readonly Color Muted = Color.FromArgb(0x8B, 0x94, 0xA1);
        public static readonly Color Green = Color.FromArgb(0x35, 0xD8, 0x95);
        public static readonly Color Teal = Color.FromArgb(0x12, 0x9A, 0x8E);
        public static readonly Color Amber = Color.FromArgb(0xF4, 0xB5, 0x40);
        public static readonly Color Red = Color.FromArgb(0xF0, 0x5D, 0x5D);
        public static readonly Color Idle = Color.FromArgb(0x5E, 0x67, 0x75);
        public static float K = 1f;

        public static int S(int v) { return (int)Math.Round(v * K); }

        public static Color For(VState s)
        {
            switch (s)
            {
                case VState.On: return Green;
                case VState.Connecting: return Amber;
                case VState.Blocked: return Red;
                default: return Idle;
            }
        }

        static Color Darken(Color c, float f)
        {
            return Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// Знак AngardVPN: щит с буквой «А» из двух клинков и точкой-остриём («ан гард» — стойка фехтовальщика).
        /// Цвет щита зависит от состояния, поэтому тот же знак служит значком в трее.
        public static void DrawLogo(Graphics g, RectangleF b, VState state)
        {
            // Выключенное состояние светлее обычного серого, чтобы значок читался на тёмной панели задач.
            Color top = state == VState.On ? Green : state == VState.Off || state == VState.Stopping ? Color.FromArgb(0xA4, 0xAC, 0xB8) : For(state);
            Color bottom = state == VState.On ? Teal : Darken(top, 0.62f);
            DrawLogo(g, b, top, bottom);
        }

        /// Контур щита, вписанный в прямоугольник.
        public static GraphicsPath ShieldPath(RectangleF b)
        {
            float x = b.X, y = b.Y, w = b.Width, h = b.Height;
            GraphicsPath p = new GraphicsPath();
            p.AddLine(x + 0.50f * w, y + 0.03f * h, x + 0.90f * w, y + 0.17f * h);
            p.AddBezier(x + 0.90f * w, y + 0.17f * h, x + 0.90f * w, y + 0.60f * h, x + 0.80f * w, y + 0.83f * h, x + 0.50f * w, y + 0.98f * h);
            p.AddBezier(x + 0.50f * w, y + 0.98f * h, x + 0.20f * w, y + 0.83f * h, x + 0.10f * w, y + 0.60f * h, x + 0.10f * w, y + 0.17f * h);
            p.CloseFigure();
            return p;
        }

        public static void DrawLogo(Graphics g, RectangleF b, Color top, Color bottom)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = b.X, y = b.Y, w = b.Width, h = b.Height;
            using (GraphicsPath p = ShieldPath(b))
            {
                using (LinearGradientBrush br = new LinearGradientBrush(new PointF(x, y), new PointF(x, y + h), top, bottom))
                    g.FillPath(br, p);
            }
            Color ink = Color.FromArgb(0x0D, 0x12, 0x17);
            using (Pen pen = new Pen(ink, Math.Max(1.4f, w * 0.115f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[] {
                    new PointF(x + 0.30f * w, y + 0.66f * h),
                    new PointF(x + 0.50f * w, y + 0.27f * h),
                    new PointF(x + 0.70f * w, y + 0.66f * h) });
            }
            float r = Math.Max(0.9f, w * 0.068f);
            using (Brush dot = new SolidBrush(ink))
                g.FillEllipse(dot, x + 0.50f * w - r, y + 0.635f * h - r, 2 * r, 2 * r);
            g.SmoothingMode = old;
        }

        public static void DrawPower(Graphics g, RectangleF box, Color c, float pen)
        {
            using (Pen p = new Pen(c, pen))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, box, -60, 300);
                float cx = box.X + box.Width / 2;
                g.DrawLine(p, cx, box.Y - box.Height * 0.08f, cx, box.Y + box.Height * 0.42f);
            }
        }
    }

    /// Крестик и «свернуть» в заголовке.
    class GlyphButton : Control
    {
        public bool IsClose;
        public bool IsPin;          // «поверх всех окон»; Active — включено
        public bool IsCompact;      // свернуть окно в одну строку
        public bool Active;
        bool hover;

        public GlyphButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(hover ? (IsClose ? Theme.Red : Theme.PanelHi) : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = Width / 2, cy = Height / 2, r = Theme.S(5);
            if (IsPin) { Glyphs.Pin(g, cx, cy, Active ? Theme.Green : hover ? Theme.Text : Theme.Muted); return; }
            if (IsCompact) { Glyphs.Strip(g, cx, cy, hover ? Theme.Text : Theme.Muted, false); return; }
            using (Pen p = new Pen(hover ? Theme.Text : Theme.Muted, Math.Max(1f, Theme.K * 1.3f)))
            {
                if (IsClose)
                {
                    g.DrawLine(p, cx - r, cy - r, cx + r, cy + r);
                    g.DrawLine(p, cx + r, cy - r, cx - r, cy + r);
                }
                else g.DrawLine(p, cx - r, cy + r - 1, cx + r, cy + r - 1);
            }
        }
    }

    /// Переключатель с подписью.
    class Toggle : Control
    {
        bool on;
        public event EventHandler Changed;

        public Toggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
        }

        public bool On
        {
            get { return on; }
            set { on = value; Invalidate(); }
        }

        protected override void OnClick(EventArgs e)
        {
            on = !on;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Theme.S(34), h = Theme.S(18);
            Rectangle pill = new Rectangle(Width - w - 1, (Height - h) / 2, w, h);
            using (GraphicsPath p = Theme.Round(pill, h / 2))
            using (Brush b = new SolidBrush(on ? Theme.Green : Theme.Border))
                g.FillPath(b, p);
            int d = h - Theme.S(6);
            int x = on ? pill.Right - d - Theme.S(3) : pill.X + Theme.S(3);
            using (Brush b = new SolidBrush(on ? Color.FromArgb(0x0D, 0x12, 0x17) : Theme.Muted))
                g.FillEllipse(b, x, pill.Y + Theme.S(3), d, d);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - w - Theme.S(8), Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// Мелкие значки для кнопок окна.
    static class Glyphs
    {
        /// Канцелярская кнопка: «поверх всех окон».
        public static void Pin(Graphics g, float cx, float cy, Color c)
        {
            float k = Theme.K;
            using (Pen p = new Pen(c, Math.Max(1.2f, 1.4f * k)))
            using (Brush b = new SolidBrush(c))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                // Головка, «юбка» и игла, наклонённые на 45°.
                GraphicsState st = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(45);
                g.FillRectangle(b, -2.5f * k, -6.5f * k, 5f * k, 5.5f * k);
                g.DrawLine(p, -4.5f * k, -1f * k, 4.5f * k, -1f * k);
                g.DrawLine(p, 0, -1f * k, 0, 6.5f * k);
                g.Restore(st);
            }
        }

        /// Окно-полоска (свернуть в строку) либо окно целиком (развернуть).
        public static void Strip(Graphics g, float cx, float cy, Color c, bool full)
        {
            float k = Theme.K, w = 12 * k, h = full ? 10 * k : 5 * k;
            using (Pen p = new Pen(c, Math.Max(1.2f, 1.3f * k)))
            using (GraphicsPath r = Theme.Round(new Rectangle((int)(cx - w / 2), (int)(cy - h / 2), (int)w, (int)h), Math.Max(1, (int)(2 * k))))
                g.DrawPath(p, r);
        }
    }

    /// Окно, свёрнутое в одну строку: кнопка подключения, страна, скорость; справа — «поверх окон» и «развернуть».
    /// За пустое место строку можно перетаскивать.
    class MiniBar : Control
    {
        public VState State;
        public string Code = "", Title = "", Speed = "";
        public Color TitleColor = Theme.Text;
        public bool Pinned;
        public event EventHandler PowerClicked, PinClicked, ExpandClicked;

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);

        int hot = -1;     // 0 — кнопка, 1 — «поверх», 2 — «развернуть»
        Font badge, small;

        public MiniBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        Rectangle PowerRect { get { int d = Theme.S(30); return new Rectangle(Theme.S(8), (Height - d) / 2, d, d); } }
        Rectangle ExpandRect { get { int d = Theme.S(28); return new Rectangle(Width - d - Theme.S(6), (Height - d) / 2, d, d); } }
        Rectangle PinRect { get { Rectangle e = ExpandRect; return new Rectangle(e.X - e.Width - Theme.S(2), e.Y, e.Width, e.Height); } }

        int HitTest(Point p)
        {
            if (PowerRect.Contains(p)) return 0;
            if (PinRect.Contains(p)) return 1;
            if (ExpandRect.Contains(p)) return 2;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.SizeAll; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || HitTest(e.Location) >= 0) return;
            Form f = FindForm();
            if (f == null) return;
            ReleaseCapture();
            SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);     // WM_NCLBUTTONDOWN, HTCAPTION: перетаскивание окна
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            EventHandler h = null;
            switch (HitTest(e.Location)) { case 0: h = PowerClicked; break; case 1: h = PinClicked; break; case 2: h = ExpandClicked; break; }
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (badge == null)
            {
                badge = new Font(Font.FontFamily, Font.SizeInPoints - 1.5f, FontStyle.Bold);
                small = new Font(Font.FontFamily, Font.SizeInPoints - 0.5f);
            }
            Color sc = Theme.For(State);
            Rectangle pw = PowerRect;
            using (Brush b = new SolidBrush(hot == 0 ? Theme.PanelHi : Theme.Panel)) g.FillEllipse(b, pw);
            using (Pen ring = new Pen(sc, Math.Max(1.5f, 2f * Theme.K))) g.DrawEllipse(ring, pw.X + 1, pw.Y + 1, pw.Width - 2, pw.Height - 2);
            float m = pw.Width * 0.3f;
            Theme.DrawPower(g, new RectangleF(pw.X + m, pw.Y + m + 1, pw.Width - 2 * m, pw.Width - 2 * m), State == VState.Off ? Theme.Text : sc, Math.Max(1.5f, 1.8f * Theme.K));

            int x = pw.Right + Theme.S(10);
            if (!string.IsNullOrEmpty(Code))
            {
                Rectangle bd = new Rectangle(x, (Height - Theme.S(20)) / 2, Theme.S(30), Theme.S(20));
                using (GraphicsPath p = Theme.Round(bd, Theme.S(5)))
                using (Brush b = new SolidBrush(Theme.Border))
                    g.FillPath(b, p);
                TextRenderer.DrawText(g, Code, badge, bd, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x = bd.Right + Theme.S(8);
            }
            int right = PinRect.X - Theme.S(6);
            int sw = string.IsNullOrEmpty(Speed) ? 0 : TextRenderer.MeasureText(g, Speed, small, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextFormatFlags vf = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            if (sw > 0) TextRenderer.DrawText(g, Speed, small, new Rectangle(right - sw, 0, sw, Height), Theme.Green, vf | TextFormatFlags.Right);
            TextRenderer.DrawText(g, Title, Font, new Rectangle(x, 0, Math.Max(0, right - sw - Theme.S(8) - x), Height), TitleColor, vf | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            Rectangle pr = PinRect, er = ExpandRect;
            if (hot == 1) using (Brush b = new SolidBrush(Theme.PanelHi)) using (GraphicsPath p = Theme.Round(pr, Theme.S(6))) g.FillPath(b, p);
            if (hot == 2) using (Brush b = new SolidBrush(Theme.PanelHi)) using (GraphicsPath p = Theme.Round(er, Theme.S(6))) g.FillPath(b, p);
            Glyphs.Pin(g, pr.X + pr.Width / 2f, pr.Y + pr.Height / 2f, Pinned ? Theme.Green : hot == 1 ? Theme.Text : Theme.Muted);
            Glyphs.Strip(g, er.X + er.Width / 2f, er.Y + er.Height / 2f, hot == 2 ? Theme.Text : Theme.Muted, true);
        }
    }

    class ServerRow
    {
        public VpnServer Server;      // null — «Лучший доступный»
        public string Code, Title, Sub, Right1, Right2;
        public Color Right2Color = Theme.Muted;
        public Color SubColor = Theme.Muted;
        public bool Pinned;           // текущий сервер: закреплён наверху и подсвечен
    }

    /// Строка текущего сервера: показывает, куда идёт подключение, и раскрывает список по щелчку.
    class ServerBar : Control
    {
        public ServerRow Row;
        public bool Open;
        bool hover;
        Font small, badge;

        public ServerBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Height > 0)
                using (GraphicsPath p = Theme.Round(new Rectangle(0, 0, Width, Height), Theme.S(10)))
                    Region = new Region(p);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(hover || Open ? Theme.PanelHi : Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (small == null)
            {
                small = new Font(Font.FontFamily, Font.SizeInPoints - 1.2f);
                badge = new Font(Font.FontFamily, Font.SizeInPoints - 1.5f, FontStyle.Bold);
            }
            ServerRow r = Row;
            int h = Height, pad = Theme.S(12), chev = Theme.S(30), rightW = Theme.S(84);
            if (r != null)
            {
                Rectangle bd = new Rectangle(pad, (h - Theme.S(22)) / 2, Theme.S(32), Theme.S(22));
                if (r.Server == null)
                    Theme.DrawLogo(g, new RectangleF(bd.X + (bd.Width - bd.Height) / 2f, bd.Y - 1, bd.Height + 2, bd.Height + 2), VState.On);
                else
                {
                    using (GraphicsPath p = Theme.Round(bd, Theme.S(5)))
                    using (Brush b = new SolidBrush(Theme.Border))
                        g.FillPath(b, p);
                    TextRenderer.DrawText(g, r.Code, badge, bd, Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                int tx = bd.Right + Theme.S(10);
                int tw = Width - tx - rightW - chev - Theme.S(4);
                TextFormatFlags lf = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, r.Title, Font, new Rectangle(tx, Theme.S(10), tw, Theme.S(18)), Theme.Text, lf);
                TextRenderer.DrawText(g, r.Sub, small, new Rectangle(tx, Theme.S(29), tw, Theme.S(18)), r.SubColor, lf);
                TextFormatFlags rf = TextFormatFlags.Right | TextFormatFlags.NoPadding;
                Rectangle rr = new Rectangle(Width - rightW - chev, Theme.S(10), rightW, Theme.S(18));
                TextRenderer.DrawText(g, r.Right1 ?? "", small, rr, Theme.Text, rf);
                rr.Y = Theme.S(29);
                TextRenderer.DrawText(g, r.Right2 ?? "", small, rr, r.Right2Color, rf);
            }
            // Стрелка: вниз — список закрыт, вверх — открыт.
            float cx = Width - chev / 2f - Theme.S(4), cy = h / 2f, a = Theme.S(5), dir = Open ? -1 : 1;
            using (Pen p = new Pen(Theme.Muted, Math.Max(1.4f, Theme.K * 1.6f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[] { new PointF(cx - a, cy - dir * a / 2), new PointF(cx, cy + dir * a / 2), new PointF(cx + a, cy - dir * a / 2) });
            }
        }
    }

    /// Маленькая кнопка-значок в нижней строке окна.
    class IconButton : Control
    {
        public bool IsGear;       // иначе — «обновить»
        bool hover;

        public IconButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hover)
                using (GraphicsPath bg = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(8)))
                using (Brush b = new SolidBrush(Theme.PanelHi))
                    g.FillPath(b, bg);
            float cx = Width / 2f, cy = Height / 2f, r = Theme.S(7);
            using (Pen p = new Pen(hover ? Theme.Text : Theme.Muted, Math.Max(1.4f, Theme.K * 1.6f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                if (IsGear)
                {
                    // Шестерёнка: сплошное зубчатое колесо с отверстием посередине.
                    float r1 = r * 1.28f, r2 = r * 0.94f;
                    PointF[] pts = new PointF[32];
                    double[] off = { -15, -8.5, 8.5, 15 };
                    for (int i = 0; i < 8; i++)
                        for (int k = 0; k < 4; k++)
                        {
                            double a = (i * 45 + off[k] - 90) * Math.PI / 180;
                            float rad = k == 1 || k == 2 ? r1 : r2;
                            pts[i * 4 + k] = new PointF(cx + (float)Math.Cos(a) * rad, cy + (float)Math.Sin(a) * rad);
                        }
                    using (GraphicsPath gear = new GraphicsPath(FillMode.Alternate))
                    using (Brush fill = new SolidBrush(hover ? Theme.Text : Theme.Muted))
                    {
                        gear.AddPolygon(pts);
                        gear.AddEllipse(cx - r * 0.42f, cy - r * 0.42f, r * 0.84f, r * 0.84f);
                        g.FillPath(fill, gear);
                    }
                }
                else
                {
                    g.DrawArc(p, cx - r, cy - r, 2 * r, 2 * r, -50, 290);
                    float ax = cx + r * (float)Math.Cos(-50 * Math.PI / 180), ay = cy + r * (float)Math.Sin(-50 * Math.PI / 180);
                    g.DrawLine(p, ax, ay, ax - r * 0.55f, ay - r * 0.05f);
                    g.DrawLine(p, ax, ay, ax + r * 0.05f, ay - r * 0.60f);
                }
            }
        }
    }

    /// Список серверов в тёмном стиле с собственной прокруткой.
    class ServerListView : Control
    {
        public readonly List<ServerRow> Rows = new List<ServerRow>();
        int selected, hoverRow = -1, scroll;
        bool dragging;
        Font small, badge;
        public event EventHandler SelectionChanged;

        public ServerListView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Height > 0)
                using (GraphicsPath p = Theme.Round(new Rectangle(0, 0, Width, Height), Theme.S(10)))
                    Region = new Region(p);
        }

        int RowH { get { return Theme.S(44); } }
        int MaxScroll { get { return Math.Max(0, Rows.Count * RowH - Height); } }

        public int SelectedIndex
        {
            get { return selected; }
            set { selected = Math.Max(0, Math.Min(value, Rows.Count - 1)); Invalidate(); }
        }

        public ServerRow SelectedRow
        {
            get { return selected >= 0 && selected < Rows.Count ? Rows[selected] : null; }
        }

        public void Reset()
        {
            scroll = Math.Min(scroll, MaxScroll);
            if (selected >= Rows.Count) selected = 0;
            Invalidate();
        }

        public void ScrollTop()
        {
            scroll = 0;
            Invalidate();
        }

        public void EnsureVisible()
        {
            int top = selected * RowH;
            if (top < scroll) scroll = top;
            else if (top + RowH > scroll + Height) scroll = top + RowH - Height;
            scroll = Math.Max(0, Math.Min(scroll, MaxScroll));
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverRow = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(scroll - Math.Sign(e.Delta) * RowH * 2, MaxScroll));
            Invalidate();
        }

        void DragTo(int y)
        {
            if (MaxScroll == 0) return;
            scroll = Math.Max(0, Math.Min((int)((long)y * (Rows.Count * RowH) / Height) - Height / 2, MaxScroll));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.X >= Width - Theme.S(12) && MaxScroll > 0) { dragging = true; DragTo(e.Y); return; }
            int i = (e.Y + scroll) / RowH;
            if (i < 0 || i >= Rows.Count) return;
            if (i != selected)
            {
                selected = i;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            // Щелчок мышью по строке — команда «подключиться сюда» (стрелки на клавиатуре только выбирают).
            if (RowClicked != null) RowClicked(this, EventArgs.Empty);
        }

        public event EventHandler RowClicked;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) { DragTo(e.Y); return; }
            int i = (e.Y + scroll) / RowH;
            if (i != hoverRow) { hoverRow = i; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int d = e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.Up ? -1 : 0;
            if (d == 0 || Rows.Count == 0) return;
            int n = Math.Max(0, Math.Min(selected + d, Rows.Count - 1));
            if (n == selected) return;
            selected = n;
            EnsureVisible();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (small == null)
            {
                small = new Font(Font.FontFamily, Font.SizeInPoints - 1.2f);
                badge = new Font(Font.FontFamily, Font.SizeInPoints - 1.5f, FontStyle.Bold);
            }
            if (Rows.Count == 0)
            {
                TextRenderer.DrawText(g, "Загрузка списка серверов…", Font, ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int h = RowH, pad = Theme.S(12), rightW = Theme.S(92);
            for (int i = scroll / h; i < Rows.Count; i++)
            {
                int y = i * h - scroll;
                if (y > Height) break;
                ServerRow r = Rows[i];
                Rectangle row = new Rectangle(0, y, Width, h);
                if (r.Pinned)
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(34, r.SubColor))) g.FillRectangle(b, row);
                    using (Brush b = new SolidBrush(r.SubColor)) g.FillRectangle(b, 0, y, Theme.S(3), h);
                }
                else if (i == selected)
                {
                    using (Brush b = new SolidBrush(Theme.PanelHi)) g.FillRectangle(b, row);
                    using (Brush b = new SolidBrush(Theme.Green)) g.FillRectangle(b, 0, y, Theme.S(3), h);
                }
                else if (i == hoverRow)
                    using (Brush b = new SolidBrush(Color.FromArgb(0x1E, 0x23, 0x2B))) g.FillRectangle(b, row);

                Rectangle bd = new Rectangle(pad, y + (h - Theme.S(22)) / 2, Theme.S(32), Theme.S(22));
                if (r.Server == null)
                    Theme.DrawLogo(g, new RectangleF(bd.X + (bd.Width - bd.Height) / 2f, bd.Y - 1, bd.Height + 2, bd.Height + 2), VState.On);
                else
                {
                    using (GraphicsPath p = Theme.Round(bd, Theme.S(5)))
                    using (Brush b = new SolidBrush(Theme.Border))
                        g.FillPath(b, p);
                    TextRenderer.DrawText(g, r.Code, badge, bd, Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }

                int tx = bd.Right + Theme.S(10);
                int tw = Width - tx - rightW - pad;
                TextFormatFlags lf = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, r.Title, Font, new Rectangle(tx, y + Theme.S(5), tw, h / 2), Theme.Text, lf);
                TextRenderer.DrawText(g, r.Sub, small, new Rectangle(tx, y + h / 2 + Theme.S(1), tw, h / 2), r.SubColor, lf);
                TextFormatFlags rf = TextFormatFlags.Right | TextFormatFlags.NoPadding;
                Rectangle rr = new Rectangle(Width - rightW - pad, y + Theme.S(5), rightW, h / 2);
                TextRenderer.DrawText(g, r.Right1, small, rr, Theme.Text, rf);
                rr.Y = y + h / 2 + Theme.S(1);
                TextRenderer.DrawText(g, r.Right2, small, rr, r.Right2Color, rf);
            }
            if (MaxScroll > 0)
            {
                int total = Rows.Count * h;
                int th = Math.Max(Theme.S(24), Height * Height / total);
                int ty = (int)((long)scroll * (Height - th) / MaxScroll);
                using (Brush b = new SolidBrush(Theme.Idle))
                    g.FillRectangle(b, Width - Theme.S(4), ty, Theme.S(3), th);
            }
        }
    }
}
