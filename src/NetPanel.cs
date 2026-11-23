using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace MiniVpn
{
    /// Панель под кнопкой подключения: URL-тест (отклик четырёх популярных сайтов) и живой монитор отклика
    /// до выбранного сайта. Измеряется тем же путём, что и весь трафик: при включённом VPN — через туннель.
    class NetPanel : Control
    {
        static readonly string[] Names = { "Google", "YouTube", "Instagram", "Telegram" };
        static readonly string[] Urls = {
            "https://www.google.com/generate_204", "https://www.youtube.com/generate_204",
            "https://www.instagram.com/", "https://telegram.org/" };
        const int Samples = 60;

        readonly int[] url = { -2, -2, -2, -2 };          // мс; -2 — ещё не измерено, -1 — нет ответа
        readonly int[] history = new int[Samples];         // монитор: последние замеры, -2 — пусто
        int head;
        volatile string host = "";
        volatile bool running, urlDue = true;
        volatile int epoch;                                // смена сайта или состояния сбрасывает график
        Rectangle hostRect;
        bool hoverHost;
        Font small, caps;
        public event EventHandler HostClicked;

        public NetPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            for (int i = 0; i < Samples; i++) history[i] = -2;
        }

        public string Host
        {
            get { return host; }
            set { host = value ?? ""; ResetHistory(); }
        }

        /// Измерять только пока окно на экране.
        public bool Active
        {
            get { return running; }
            set
            {
                if (running == value) return;
                running = value;
                if (!value) return;
                urlDue = true;
                Thread m = new Thread(MonitorLoop); m.IsBackground = true; m.Start();
                Thread u = new Thread(UrlLoop); u.IsBackground = true; u.Start();
            }
        }

        /// Путь трафика изменился (подключение, отключение): старые цифры больше ничего не значат.
        public void Restart()
        {
            for (int i = 0; i < url.Length; i++) url[i] = -2;
            ResetHistory();
            urlDue = true;
        }

        void ResetHistory()
        {
            epoch++;
            lock (history) { for (int i = 0; i < Samples; i++) history[i] = -2; head = 0; }
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Height > 0)
                using (GraphicsPath p = Theme.Round(new Rectangle(0, 0, Width, Height), Theme.S(10)))
                    Region = new Region(p);
        }

        void Redraw()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)Invalidate); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// Время соединения с сайтом по порту 443, мс; -1 — не ответил.
        static int Connect(string h, int timeoutMs)
        {
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(h, 443, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return -1;
                    c.EndConnect(ar);
                }
                return (int)sw.ElapsedMilliseconds;
            }
            catch (Exception) { return -1; }
        }

        void MonitorLoop()
        {
            while (running && !IsDisposed)
            {
                string h = host;
                int my = epoch;
                int ms = h.Length == 0 ? -2 : Connect(h, 2500);
                if (my == epoch)
                {
                    lock (history) { history[head] = ms; head = (head + 1) % Samples; }
                    Redraw();
                }
                Thread.Sleep(1000);
            }
        }

        void UrlLoop()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }   // TLS 1.2
            DateTime last = DateTime.MinValue;
            while (running && !IsDisposed)
            {
                if (urlDue || (DateTime.Now - last).TotalSeconds >= 30)
                {
                    urlDue = false;
                    last = DateTime.Now;
                    for (int i = 0; i < Urls.Length && running; i++)
                    {
                        url[i] = UrlTime(Urls[i]);
                        Redraw();
                    }
                }
                Thread.Sleep(250);
            }
        }

        /// Время до первого ответа сайта, мс; -1 — не ответил. Любой ответ сервера считается успехом.
        static int UrlTime(string u)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(u);
                r.Method = "HEAD";
                r.Timeout = 5000;
                r.Proxy = null;
                r.KeepAlive = false;
                r.AllowAutoRedirect = false;
                r.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                using (r.GetResponse()) { }
                return (int)sw.ElapsedMilliseconds;
            }
            catch (WebException e) { return e.Response != null ? (int)sw.ElapsedMilliseconds : -1; }
            catch (Exception) { return -1; }
        }

        static Color Tone(int ms, int good, int ok)
        {
            return ms == -2 ? Theme.Idle : ms < 0 ? Theme.Red : ms <= good ? Theme.Green : ms <= ok ? Theme.Amber : Theme.Red;
        }

        static string Show(int ms)
        {
            return ms == -2 ? "…" : ms < 0 ? "нет" : ms + " мс";
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = hostRect.Contains(e.Location);
            if (h != hoverHost) { hoverHost = h; Invalidate(); }
            Cursor = h || e.Y < Height / 2 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hoverHost = false; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (hostRect.Contains(e.Location)) { if (HostClicked != null) HostClicked(this, EventArgs.Empty); }
            else if (e.Y < Height / 2) { for (int i = 0; i < url.Length; i++) url[i] = -2; urlDue = true; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (small == null)
            {
                small = new Font(Font.FontFamily, Font.SizeInPoints - 1.2f);
                caps = new Font("Segoe UI Semibold", 7.5f);
            }
            int pad = Theme.S(12), half = Height / 2;
            TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextFormatFlags right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

            // Верхняя половина: URL-тест, четыре сайта. Щелчок запускает замер заново.
            int cell = (Width - 2 * pad) / Names.Length;
            for (int i = 0; i < Names.Length; i++)
            {
                Rectangle c = new Rectangle(pad + i * cell, Theme.S(4), cell, half - Theme.S(4));
                TextRenderer.DrawText(g, Names[i], caps, new Rectangle(c.X, c.Y, c.Width, c.Height / 2), Theme.Muted, left);
                TextRenderer.DrawText(g, Show(url[i]), small, new Rectangle(c.X, c.Y + c.Height / 2 - Theme.S(1), c.Width, c.Height / 2), Tone(url[i], 400, 1200), left);
            }
            using (Pen sep = new Pen(Theme.Border))
                g.DrawLine(sep, pad, half, Width - pad, half);

            // Нижняя половина: монитор — сайт, график последних 60 секунд, текущее значение.
            int[] h = new int[Samples];
            int now;
            lock (history)
            {
                for (int i = 0; i < Samples; i++) h[i] = history[(head + i) % Samples];
                now = h[Samples - 1];
            }
            string name = host.Length == 0 ? "выбрать сайт" : host;
            int nameW = Math.Min(Theme.S(120), TextRenderer.MeasureText(g, name, small, Size.Empty, TextFormatFlags.NoPadding).Width + 2);
            hostRect = new Rectangle(pad, half + Theme.S(4), nameW, half - Theme.S(8));
            TextRenderer.DrawText(g, "МОНИТОР", caps, new Rectangle(pad, half + Theme.S(3), nameW, Theme.S(13)), Theme.Muted, left);
            TextRenderer.DrawText(g, name, small, new Rectangle(pad, half + Theme.S(15), nameW, Theme.S(17)), hoverHost ? Theme.Text : Theme.Muted, left);
            int valW = Theme.S(58);
            TextRenderer.DrawText(g, Show(now), Font, new Rectangle(Width - pad - valW, half, valW, half), Tone(now, 150, 500), right);

            RectangleF plot = new RectangleF(pad + nameW + Theme.S(10), half + Theme.S(8), Width - 2 * pad - nameW - valW - Theme.S(16), half - Theme.S(16));
            int max = 100;
            foreach (int v in h) if (v > max) max = v;
            float step = plot.Width / (Samples - 1);
            using (Pen line = new Pen(Tone(now, 150, 500), Math.Max(1.3f, Theme.K * 1.4f)))
            using (Pen miss = new Pen(Theme.Red, Math.Max(1.3f, Theme.K * 1.4f)))
            {
                line.LineJoin = LineJoin.Round;
                PointF prev = PointF.Empty;
                bool has = false;
                for (int i = 0; i < Samples; i++)
                {
                    float x = plot.Left + i * step;
                    if (h[i] == -1) { g.DrawLine(miss, x, plot.Bottom - Theme.S(3), x, plot.Bottom); has = false; continue; }
                    if (h[i] < 0) { has = false; continue; }
                    PointF p = new PointF(x, plot.Bottom - plot.Height * h[i] / max);
                    if (has) g.DrawLine(line, prev, p);
                    prev = p;
                    has = true;
                }
            }
        }
    }

    /// Маленькое окно для ввода одной строки (адрес сайта для монитора).
    class InputForm : DarkDialog
    {
        readonly TextBox box = new TextBox();
        public string Value { get { return box.Text.Trim(); } }

        public InputForm(string title, string value) : base(title, 300, 132)
        {
            Panel frame = new Panel();
            frame.BackColor = Theme.PanelHi;
            frame.SetBounds(Theme.S(16), Theme.S(52), ClientSize.Width - Theme.S(32), Theme.S(30));
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Theme.PanelHi;
            box.ForeColor = Theme.Text;
            box.Font = Font;
            box.Text = value;
            box.SetBounds(Theme.S(8), Theme.S(6), frame.Width - Theme.S(16), Theme.S(20));
            box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; Close(); }
            };
            frame.Controls.Add(box);
            Controls.Add(frame);
            Button("Готово", 176, 90, 108, 30, delegate { DialogResult = DialogResult.OK; Close(); });
            Shown += delegate { box.Focus(); box.SelectAll(); };
        }
    }
}
