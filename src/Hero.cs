using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MiniVpn
{
    /// Строка текста, которую рисует Hero. Ведёт себя как Label, чтобы логика окна не зависела от способа отрисовки.
    class TextSlot
    {
        readonly Control owner;
        string text = "";
        Color color;
        Cursor cursor = Cursors.Default;
        bool visible = true;

        public TextSlot(Control owner, Color color) { this.owner = owner; this.color = color; }

        public string Text { get { return text; } set { text = value ?? ""; owner.Invalidate(); } }
        public Color ForeColor { get { return color; } set { color = value; owner.Invalidate(); } }
        public Cursor Cursor { get { return cursor; } set { cursor = value; } }     // Hand — строка работает как ссылка
        public bool Visible { get { return visible; } set { visible = value; owner.Invalidate(); } }
        public Rectangle Bounds;
    }

    /// Верхняя часть окна: эмблема-кнопка со щитом, статус и две строки под ним.
    /// За щитом клубится дым цвета состояния. Пока идёт подключение, вокруг щита кружат два световых росчерка,
    /// а по самому щиту пробегает блик.
    class Hero : Control
    {
        public readonly TextSlot Status, Line1, Line2, Unblock;
        public event EventHandler Pressed, Line2Clicked, UnblockClicked, RefreshClicked;

        bool busy, hoverRefresh;
        Rectangle refreshRect;
        Font refreshFont;

        /// Идёт обновление списка серверов: на кнопке в углу вместо «Обновить» — «Загрузка…».
        public bool Busy { get { return busy; } set { busy = value; Invalidate(); } }

        VState state = VState.Off;
        bool hover, down;
        double clock;                // секунды анимации
        readonly Timer anim = new Timer();
        Font statusFont, linkFont;
        DateTime last = DateTime.Now;

        public Hero()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Status = new TextSlot(this, Theme.Text);
            Line1 = new TextSlot(this, Theme.Muted);
            Line2 = new TextSlot(this, Theme.Muted);
            Unblock = new TextSlot(this, Theme.Red);
            Unblock.Visible = false;
            anim.Tick += delegate
            {
                DateTime now = DateTime.Now;
                clock += Math.Min(0.1, (now - last).TotalSeconds);
                last = now;
                Invalidate(new Rectangle(0, 0, Width, Theme.S(190)));
            };
        }

        public VState State
        {
            get { return state; }
            set
            {
                state = value;
                bool run = value == VState.Connecting || value == VState.On || value == VState.Blocked;
                anim.Interval = value == VState.Connecting ? 20 : 40;
                last = DateTime.Now;
                if (run != anim.Enabled) anim.Enabled = run;
                Invalidate();
            }
        }

        /// Для снимков: показать конкретный момент анимации (секунды).
        public double Clock { get { return clock; } set { clock = value; Invalidate(); } }

        PointF Center { get { return new PointF(Width / 2f, Theme.S(104)); } }
        float R { get { return Theme.S(64); } }

        bool OnEmblem(Point p)
        {
            float dx = p.X - Center.X, dy = p.Y - Center.Y;
            return dx * dx + dy * dy <= (R + Theme.S(6)) * (R + Theme.S(6));
        }

        bool OnLink(Point p)
        {
            return (Line2.Cursor == Cursors.Hand && Line2.Bounds.Contains(p)) || (Unblock.Visible && Unblock.Bounds.Contains(p)) || (!busy && refreshRect.Contains(p));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = OnEmblem(e.Location);
            Cursor = h || OnLink(e.Location) ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
            bool rh = refreshRect.Contains(e.Location);
            if (rh != hoverRefresh) { hoverRefresh = rh; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; hoverRefresh = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (OnEmblem(e.Location)) { down = true; Invalidate(); } base.OnMouseDown(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool fire = down && OnEmblem(e.Location) && e.Button == MouseButtons.Left;
            down = false;
            Invalidate();
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (fire) { if (Pressed != null) Pressed(this, EventArgs.Empty); }
            else if (!busy && refreshRect.Contains(e.Location)) { if (RefreshClicked != null) RefreshClicked(this, EventArgs.Empty); }
            else if (Unblock.Visible && Unblock.Bounds.Contains(e.Location)) { if (UnblockClicked != null) UnblockClicked(this, EventArgs.Empty); }
            else if (Line2.Cursor == Cursors.Hand && Line2.Bounds.Contains(e.Location)) { if (Line2Clicked != null) Line2Clicked(this, EventArgs.Empty); }
        }

        static double Frac(double v) { return v - Math.Floor(v); }

        static void Puff(Graphics g, float x, float y, float rx, float ry, Color c, int alpha)
        {
            if (alpha <= 0) return;
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(x - rx, y - ry, 2 * rx, 2 * ry);
                using (PathGradientBrush pg = new PathGradientBrush(gp))
                {
                    pg.CenterColor = Color.FromArgb(Math.Min(255, alpha), c);
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, c) };
                    g.FillPath(pg, gp);
                }
            }
        }

        /// Дым: клубы рождаются за нижней частью щита, поднимаются, покачиваясь, расширяются и тают.
        void DrawSmoke(Graphics g, PointF c, float r, Color col, int strength)
        {
            const int count = 10;
            for (int i = 0; i < count; i++)
            {
                double seed = i * 1.618;
                double life = Frac(clock / 7.5 + i / (double)count);
                float side = (float)Math.Cos(seed * 5.1) * r * 0.55f;
                float sway = (float)Math.Sin(life * 5.2 + seed * 3.0) * r * (0.18f + 0.50f * (float)life);
                float x = c.X + side + sway;
                float y = c.Y + r * 0.55f - (float)life * r * 1.50f;
                float rad = r * (0.55f + 0.80f * (float)life);
                float squash = 0.78f + 0.20f * (float)Math.Sin(seed * 2.3 + life * 3.0);
                Puff(g, x, y, rad * 1.22f, rad * squash, col, (int)(strength * Math.Sin(life * Math.PI)));
            }
        }

        /// Световой росчерк по кольцу: тонкий хвост, яркая голова.
        void DrawComet(Graphics g, RectangleF ring, float head, Color c)
        {
            const int seg = 26;
            const float span = 130;
            for (int k = 0; k < seg; k++)
            {
                float f = (k + 1) / (float)seg;
                using (Pen p = new Pen(Color.FromArgb((int)(255 * f * f), c), Theme.S(1) + Theme.S(3) * f))
                {
                    if (k == seg - 1) p.EndCap = LineCap.Round;
                    g.DrawArc(p, ring, head - span + k * span / seg, span / seg + 0.8f);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            Color c = Theme.For(state);
            PointF ctr = Center;
            float r = R;

            // Дым за эмблемой.
            if (state == VState.On) DrawSmoke(g, ctr, r, c, 74);
            else if (state == VState.Connecting) DrawSmoke(g, ctr, r, c, 50);
            else if (state == VState.Blocked) DrawSmoke(g, ctr, r, c, 58);
            else if (hover) Puff(g, ctr.X, ctr.Y, r * 1.7f, r * 1.5f, Theme.Idle, 34);

            // Диск-кнопка.
            RectangleF disc = new RectangleF(ctr.X - r, ctr.Y - r, 2 * r, 2 * r);
            using (LinearGradientBrush fill = new LinearGradientBrush(new PointF(0, disc.Top - 1), new PointF(0, disc.Bottom + 1),
                down ? Theme.Panel : (hover ? Color.FromArgb(0x30, 0x37, 0x42) : Theme.PanelHi), down ? Theme.Bg : Theme.Panel))
                g.FillEllipse(fill, disc);
            bool quiet = state == VState.Off || state == VState.Stopping;
            using (Pen ring = new Pen(quiet ? Theme.Border : Color.FromArgb(state == VState.Connecting ? 70 : 230, c), Theme.S(2)))
                g.DrawEllipse(ring, disc);

            float sh = r * 1.22f;
            RectangleF shield = new RectangleF(ctr.X - sh * 0.47f, ctr.Y - sh * 0.5f, sh * 0.94f, sh);

            if (state == VState.Connecting)
            {
                float head = (float)(clock * 290 % 360);
                DrawComet(g, disc, head, c);
                DrawComet(g, disc, head + 180, c);

                Theme.DrawLogo(g, shield, Color.FromArgb(0x6A, 0x72, 0x7F), Color.FromArgb(0x3A, 0x41, 0x4C));
                // Блик пробегает по щиту слева направо.
                float band = shield.Width * 0.45f;
                float bx = shield.Left - band * 2 + (float)Frac(clock / 1.5) * (shield.Width + band * 4);
                using (GraphicsPath clip = Theme.ShieldPath(shield))
                {
                    GraphicsState saved = g.Save();
                    g.SetClip(clip, CombineMode.Intersect);
                    using (GraphicsPath p = new GraphicsPath())
                    {
                        float slant = shield.Height * 0.35f;
                        p.AddPolygon(new PointF[] {
                            new PointF(bx + slant, shield.Top), new PointF(bx + slant + band, shield.Top),
                            new PointF(bx + band - slant, shield.Bottom), new PointF(bx - slant, shield.Bottom) });
                        using (LinearGradientBrush lb = new LinearGradientBrush(new PointF(bx - slant - 1, 0), new PointF(bx + band + slant + 1, 0), Color.Transparent, Color.Transparent))
                        {
                            ColorBlend cb = new ColorBlend();
                            cb.Colors = new Color[] { Color.FromArgb(0, Color.White), Color.FromArgb(120, Color.White), Color.FromArgb(0, Color.White) };
                            cb.Positions = new float[] { 0f, 0.5f, 1f };
                            lb.InterpolationColors = cb;
                            g.FillPath(lb, p);
                        }
                    }
                    g.Restore(saved);
                }
            }
            else Theme.DrawLogo(g, shield, state);

            // Тексты.
            if (statusFont == null)
            {
                statusFont = new Font("Segoe UI Semibold", 14.5f);
                linkFont = new Font(Font, FontStyle.Underline);
            }
            TextFormatFlags tf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int pad = Theme.S(14), w = Width - 2 * pad;
            TextRenderer.DrawText(g, Status.Text, statusFont, new Rectangle(pad, Theme.S(192), w, Theme.S(34)), Status.ForeColor, tf);
            TextRenderer.DrawText(g, Line1.Text, Font, new Rectangle(pad, Theme.S(228), w, Theme.S(20)), Line1.ForeColor, tf);
            Line2.Bounds = new Rectangle(pad, Theme.S(249), w, Theme.S(20));
            TextRenderer.DrawText(g, Line2.Text, Line2.Cursor == Cursors.Hand ? linkFont : Font, Line2.Bounds, Line2.ForeColor, tf);
            Unblock.Bounds = new Rectangle(Theme.S(60), Theme.S(272), Width - Theme.S(120), Theme.S(20));
            if (Unblock.Visible)
                TextRenderer.DrawText(g, Unblock.Text, linkFont, Unblock.Bounds, Unblock.ForeColor, tf);

            // Кнопка обновления списка серверов — в верхнем правом углу главного экрана.
            if (refreshFont == null) refreshFont = new Font("Segoe UI Semibold", 7.5f);
            int rw = Theme.S(112), rh = Theme.S(26);
            refreshRect = new Rectangle(Width - rw - Theme.S(12), Theme.S(8), rw, rh);
            using (GraphicsPath rp = Theme.Round(refreshRect, Theme.S(13)))
            {
                using (Brush b = new SolidBrush(hoverRefresh && !busy ? Theme.PanelHi : Theme.Panel)) g.FillPath(b, rp);
                using (Pen bp = new Pen(busy ? Theme.Amber : (hoverRefresh ? Theme.Idle : Theme.Border))) g.DrawPath(bp, rp);
            }
            TextRenderer.DrawText(g, busy ? "ЗАГРУЗКА…" : "↻  ОБНОВИТЬ", refreshFont, refreshRect,
                busy ? Theme.Amber : (hoverRefresh ? Theme.Text : Theme.Muted), tf);
        }
    }
}
