using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MiniVpn
{
    class MainForm : Form
    {
        public const string Version = "2.5";
        public static readonly int ShowMessage = RegisterWindowMessage("AngardVPN_ShowWindow");
        /// «AngardVPN.exe --quit»: запущенный экземпляр отключается (мягко, с уборкой адаптера и правил) и закрывается.
        public static readonly int QuitMessage = RegisterWindowMessage("AngardVPN_Quit");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        // Перебор: сколько серверов пробовать и сколько ждать на каждом шаге одной попытки.
        const int MaxServers = 48;         // сколько серверов проверить, прежде чем сдаться
        const int RaceSize = 8;            // сколько серверов проверяется одновременно
        const int RaceMs = 8000;           // предел одной гонки
        const int RaceGraceMs = 700;       // сколько ждать остальных после первого ответившего
        int attemptNo;
        const int HandshakeMs = 6000;      // до начала обмена ключами
        const int ConfigMs = 12000;        // до получения настроек от сервера
        const int AdapterMs = 14000;       // от получения настроек до поднятого туннеля
        const int ScanMs = 2000;           // замер задержки до серверов
        const int NetCheckMs = 12000;      // сколько ждать интернета через уже поднятый туннель

        readonly Settings settings = Settings.Load();
        readonly string exe = OpenVpnEngine.FindExe();
        readonly object opLock = new object();     // сериализует изменения правил брандмауэра и запуск движка
        readonly HashSet<string> failed = new HashSet<string>();   // серверы, отказавшие в этом сеансе

        List<VpnServer> servers = new List<VpnServer>();
        VState state = VState.Off;
        volatile int gen;                          // номер сеанса; устаревшие потоки и события сверяются с ним
        volatile OpenVpnEngine engine;
        bool lastOk = true;          // последнее обновление списка было с сети (а не из сохранённой копии)
        DateTime lastTry = DateTime.MinValue, listAt = DateTime.MinValue;   // последняя попытка загрузки списка и последняя удачная
        readonly List<Action> afterLoad = new List<Action>();
        readonly System.Windows.Forms.Timer reload = new System.Windows.Forms.Timer();
        readonly ToolTip tip = new ToolTip();
        string serverIp, tunnelIp;
        string ipMethod;
        DateTime since;
        long bytesIn, bytesOut;
        bool exiting, loading;
        VpnServer active;            // сервер, закреплённый наверху списка
        bool activeOn, listCached;
        volatile bool scanning;

        readonly Hero hero = new Hero();
        readonly TextSlot lblStatus, lblServer, lblStats, lblUnblock;     // строки, которые рисует hero
        readonly Label lblSection = new Label();
        readonly ServerListView list = new ServerListView();
        readonly ServerBar bar = new ServerBar();
        readonly NetPanel net = new NetPanel();
        readonly IconButton btnRefresh = new IconButton(), btnSettings = new IconButton();
        readonly System.Windows.Forms.Timer recheck = new System.Windows.Forms.Timer();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem miToggle = new ToolStripMenuItem();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly Dictionary<VState, Icon> icons = new Dictionary<VState, Icon>();
        Font titleFont;

        class Attempt
        {
            public volatile bool Auth, Assigned, Connected, Ended;
            public volatile string TunnelIp, Stage = "";
            public long AssignedAt;
        }

        public MainForm(bool autoConnect)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Theme.K = g.DpiX / 96f;
            int S16 = Theme.S(16), W = Theme.S(360);
            ipMethod = settings.IpMethod;

            Text = "AngardVPN";
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
            titleFont = new Font("Segoe UI Semibold", 10.5f);
            ClientSize = new Size(W, Theme.S(540));
            StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - S16, wa.Bottom - Height - S16);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            GlyphButton close = new GlyphButton();
            close.IsClose = true;
            close.SetBounds(W - Theme.S(41), 1, Theme.S(40), Theme.S(34));
            close.Click += delegate { HideToTray(); };
            GlyphButton min = new GlyphButton();
            min.SetBounds(W - Theme.S(81), 1, Theme.S(40), Theme.S(34));
            min.Click += delegate { WindowState = FormWindowState.Minimized; };
            btnCompact.IsCompact = true;
            btnCompact.SetBounds(W - Theme.S(121), 1, Theme.S(40), Theme.S(34));
            btnCompact.Click += delegate { SetCompact(true); };
            btnPin.IsPin = true;
            btnPin.SetBounds(W - Theme.S(161), 1, Theme.S(40), Theme.S(34));
            btnPin.Click += delegate { SetOnTop(!settings.OnTop); };
            tip.SetToolTip(btnCompact, "Свернуть в строку");

            mini.Font = Font;
            mini.Visible = false;
            mini.PowerClicked += delegate { OnPowerClick(); };
            mini.PinClicked += delegate { SetOnTop(!settings.OnTop); };
            mini.ExpandClicked += delegate { SetCompact(false); };

            hero.SetBounds(1, Theme.S(44), W - 2, Theme.S(300));
            hero.Font = Font;
            lblStatus = hero.Status; lblServer = hero.Line1; lblStats = hero.Line2; lblUnblock = hero.Unblock;
            lblUnblock.Text = "Вернуть интернет без VPN";
            hero.Pressed += delegate { OnPowerClick(); };
            hero.RefreshClicked += delegate { LoadServers(); };
            hero.UnblockClicked += delegate { Disconnect(); };
            hero.Line2Clicked += delegate { if (reportLink != null) OpenInExplorer(reportLink); };

            Font caps = new Font("Segoe UI Semibold", 8f);
            SetupLabel(lblSection, 18, 0, 240, 18, Theme.Muted, caps, ContentAlignment.MiddleLeft);
            lblSection.Text = "СЕРВЕРЫ";

            net.Font = Font;
            net.Host = settings.MonitorHost;
            net.HostClicked += delegate { ChangeMonitorHost(); };
            VisibleChanged += delegate { net.Active = Visible && !compact; };

            bar.Font = Font;
            bar.Click += delegate { SetExpanded(!expanded); };
            btnRefresh.Click += delegate { LoadServers(); };
            btnSettings.IsGear = true;
            btnSettings.Click += delegate { OpenSettings(); };

            list.Font = Font;
            list.SelectionChanged += delegate
            {
                ServerRow r = list.SelectedRow;
                settings.Selected = r == null || r.Server == null ? "" : r.Code;     // выбор хранится как код страны
                settings.Save();
            };

            // Щелчок по серверу в раскрытом списке: список сворачивается, идёт подключение.
            list.RowClicked += delegate { SetExpanded(false); OnServerClicked(); };

            bigParts = new Control[] { close, min, btnCompact, btnPin, hero, net, bar, list, lblSection, btnRefresh, btnSettings };
            Controls.AddRange(bigParts);
            Controls.Add(mini);
            tip.SetToolTip(btnRefresh, "Обновить список серверов");
            tip.SetToolTip(btnSettings, "Настройки");
            Relayout();

            ContextMenuStrip menu = new ContextMenuStrip();
            miToggle.Click += delegate { OnPowerClick(); };
            menu.Items.Add(miToggle);
            menu.Items.Add("Открыть", null, delegate { ShowFromTray(); });
            menu.Items.Add("Настройки", null, delegate { ShowFromTray(); OpenSettings(); });
            menu.Items.Add("Создать отчёт", null, delegate { ManualReport(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            mini.ContextMenuStrip = menu;
            ApplyOnTop();
            if (settings.Compact) SetCompact(true);
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowFromTray(); };

            tick.Interval = 1000;
            tick.Tick += delegate { UpdateStats(); };
            tick.Start();

            // Раз в 30 секунд перепроверяется доступность серверов. Подключение при этом не трогается.
            recheck.Interval = 30000;
            recheck.Tick += delegate { if (state == VState.Off || state == VState.On) Scan(null); };
            recheck.Start();

            // Раз в минуту проверяется, не устарел ли список; устаревший (старше 15 минут) обновляется целиком.
            reload.Interval = 60000;
            reload.Tick += delegate
            {
                if (!loading && (state == VState.Off || state == VState.On) && (DateTime.Now - lastTry).TotalMinutes >= (lastOk ? ReloadMinutes : 2)) LoadServers();
            };
            reload.Start();

            Journal.Add("AngardVPN " + Version + " запущен, способ выдачи адреса: " + ipMethod);
            SetState(VState.Off, exe == null ? "OpenVPN не найден" : "Не подключено");
            tray.Visible = true;
            AddAutoRow();
            bool wantAuto = (autoConnect || settings.AutoConnect) && exe != null;
            LoadServers(wantAuto ? (Action)delegate { if (state == VState.Off) StartConnect(false); } : null);
        }

        public MainForm(bool autoConnect, bool hidden) : this(autoConnect)
        {
            startHidden = hidden || settings.StartHidden;
        }

        bool startHidden;

        /// Запуск свёрнутым: окно создаётся, но не показывается, пока его не откроют из трея.
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        void Balloon(int ms, string title, string text, ToolTipIcon icon)
        {
            if (settings.Notify) tray.ShowBalloonTip(ms, title, text, icon);
        }

        /// Сайт для живого монитора отклика.
        void ChangeMonitorHost()
        {
            using (InputForm f = new InputForm("Сайт для монитора", settings.MonitorHost))
            {
                f.PlaceOver(this);
                if (f.ShowDialog(this) != DialogResult.OK) return;
                settings.MonitorHost = NetCheck.CleanHost(f.Value);
                settings.Save();
                net.Host = settings.MonitorHost;
            }
        }

        void OpenSettings()
        {
            using (SettingsForm f = new SettingsForm(settings, OnKillChanged, ManualReport, delegate { LoadServers(); }, ApplyOnTop))
            {
                f.PlaceOver(this);
                f.ShowDialog(this);
            }
        }

        // ---------- раскладка: компактное окно и раскрытый список ----------

        bool expanded;

        void SetExpanded(bool on)
        {
            if (expanded == on) return;
            expanded = on;
            Relayout();
            if (on) { list.EnsureVisible(); list.Focus(); }
        }

        // ---------- строка вместо окна и «поверх всех окон» ----------

        readonly MiniBar mini = new MiniBar();
        readonly GlyphButton btnCompact = new GlyphButton(), btnPin = new GlyphButton();
        Control[] bigParts;
        bool compact;

        /// Сворачивает окно в одну строку (кнопка, страна, скорость) или разворачивает обратно.
        /// Правый нижний угол окна остаётся на месте.
        void SetCompact(bool on)
        {
            if (compact == on) return;
            compact = on;
            settings.Compact = on;
            settings.Save();
            SuspendLayout();
            foreach (Control c in bigParts) c.Visible = !on;
            mini.Visible = on;
            Relayout();     // заодно возвращает списку его видимость
            ResumeLayout();
            net.Active = Visible && !on;
            UpdateMini();
        }

        void SetOnTop(bool on)
        {
            settings.OnTop = on;
            settings.Save();
            ApplyOnTop();
        }

        void ApplyOnTop()
        {
            // Настройка включена — окно держится поверх остальных, но только пока VPN подключён.
            bool on = settings.OnTop;
            bool top = on && state == VState.On;
            if (TopMost != top) TopMost = top;
            btnPin.Active = on;
            btnPin.Invalidate();
            mini.Pinned = on;
            mini.Invalidate();
            tip.SetToolTip(btnPin, on ? "Поверх всех окон при подключении: включено" : "Поверх всех окон при подключении: выключено");
        }

        long rateIn;     // байт за последнюю секунду, для строки

        void UpdateMini()
        {
            mini.State = state;
            VpnServer s = active;
            mini.Code = s == null ? "" : s.CountryShort;
            if (state == VState.On && s != null)
            {
                mini.Title = s.CountryName;
                mini.TitleColor = Theme.Text;
                mini.Speed = "↓ " + Fmt.Bytes(rateIn) + "/с";
            }
            else
            {
                mini.Title = lblStatus.Text;
                mini.TitleColor = state == VState.Blocked ? Theme.Red : state == VState.Off ? Theme.Muted : Theme.Amber;
                mini.Speed = "";
            }
            mini.Invalidate();
        }

        /// Ставит окно так, чтобы его правый нижний угол остался на месте, но не выходил за рабочую область.
        void PlaceCorner(int right, int bottom)
        {
            Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
            Left = Math.Max(wa.Left, Math.Min(right, wa.Right) - Width);
            Top = Math.Max(wa.Top, Math.Min(bottom, wa.Bottom) - Height);
        }

        /// Расставляет нижнюю часть окна и меняет его высоту; нижний край окна остаётся на месте.
        void Relayout()
        {
            int right = Right, bottom0 = Bottom;
            if (compact)
            {
                int mw = Theme.S(300), mh = Theme.S(46);
                ClientSize = new Size(mw, mh);
                mini.SetBounds(1, 1, mw - 2, mh - 2);
                PlaceCorner(right, bottom0);
                using (GraphicsPath shape = Theme.Round(new Rectangle(0, 0, mw, mh), Theme.S(10)))
                    Region = new Region(shape);
                Invalidate();
                return;
            }
            int W = Theme.S(360), side = Theme.S(14);
            bool widthChanged = ClientSize.Width != W;
            net.SetBounds(side, Theme.S(348), W - 2 * side, Theme.S(76));
            bar.SetBounds(side, Theme.S(432), W - 2 * side, Theme.S(56));
            bar.Open = expanded;
            bar.Invalidate();
            list.SetBounds(side, Theme.S(494), W - 2 * side, Theme.S(222));
            list.Visible = expanded;
            int fy = expanded ? Theme.S(722) : Theme.S(496);
            lblSection.SetBounds(Theme.S(18), fy + Theme.S(8), W - Theme.S(110), Theme.S(18));
            btnSettings.SetBounds(W - Theme.S(48), fy, Theme.S(34), Theme.S(34));
            btnRefresh.SetBounds(W - Theme.S(86), fy, Theme.S(34), Theme.S(34));
            int bottom = Bottom, h = fy + Theme.S(44);
            bool placed = IsHandleCreated;
            ClientSize = new Size(W, h);
            if (widthChanged) PlaceCorner(right, bottom);
            else if (placed)
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                Top = Math.Max(wa.Top, bottom - Height);
            }
            using (GraphicsPath shape = Theme.Round(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Theme.S(12)))
                Region = new Region(shape);
            Invalidate();
        }

        /// Что показывает строка текущего сервера: активный сервер, иначе выбранный, иначе «Лучший доступный».
        void UpdateBar()
        {
            ServerRow sel = list.SelectedRow;
            VpnServer chosen = sel == null ? null : sel.Server;
            VpnServer show = active ?? chosen;
            ServerRow r = new ServerRow();
            r.Server = show;
            if (show == null)
            {
                r.Title = "Лучший доступный";
                r.Sub = "По отклику и скорости";
            }
            else
            {
                r.Code = show.CountryShort;
                r.Title = show.CountryName + (chosen == null ? " · авто" : "");
                r.Right1 = show.SpeedText;
                r.Right2 = Lat(show);
                r.Right2Color = LatencyColor(show);
                r.Sub = show.Ip + (show != active ? "" : activeOn ? " · подключено" : " · подключение…");
                if (show == active) r.SubColor = activeOn ? Theme.Green : Theme.Amber;
            }
            bar.Row = r;
            bar.Invalidate();
            UpdateMini();
        }

        /// Отклик для списка. До сервера, через который идёт туннель, замер изнутри туннеля не проходит —
        /// «нет ответа» для него было бы неправдой, поэтому поле остаётся пустым.
        string Lat(VpnServer s)
        {
            return s == active && s.Latency < 0 ? "" : s.LatencyText;
        }

        static Color LatencyColor(VpnServer s)
        {
            return s.Latency == -1 ? Theme.Red : s.Latency >= 0 && s.Latency < 150 ? Theme.Green : Theme.Muted;
        }

        /// Обновляет в уже показанном списке только отклик и доступность, не сбивая прокрутку.
        void RefreshRows()
        {
            // Лучший сервер страны мог смениться, поэтому строки пересобираются, но прокрутка остаётся на месте.
            keepScroll = true;
            FillList(listCached);
            keepScroll = false;
        }

        void SetupLabel(Label l, int x, int y, int w, int h, Color c, Font f, ContentAlignment a)
        {
            l.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            l.ForeColor = c;
            l.BackColor = Theme.Bg;
            l.Font = f;
            l.TextAlign = a;
            l.AutoEllipsis = true;
            l.UseMnemonic = false;
        }

        static void MakeLink(Label l, EventHandler click)
        {
            l.Cursor = Cursors.Hand;
            l.MouseEnter += delegate { l.ForeColor = Theme.Text; };
            l.MouseLeave += delegate { l.ForeColor = Theme.Muted; };
            l.Click += click;
        }

        // ---------- окно ----------

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // тень
                cp.Style |= 0x20000;        // сворачивание по щелчку в панели задач
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (compact)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Theme.Border, 2))
                using (GraphicsPath shape = Theme.Round(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Theme.S(10)))
                    g.DrawPath(pen, shape);
                return;
            }
            int s = Theme.S(22);
            Theme.DrawLogo(g, new RectangleF(Theme.S(15), Theme.S(11), s, s), VState.On);
            Point p = new Point(Theme.S(43), Theme.S(12));
            TextFormatFlags f = TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, "Angard", titleFont, p, Theme.Text, f);
            p.X += TextRenderer.MeasureText(g, "Angard", titleFont, Size.Empty, f).Width;
            TextRenderer.DrawText(g, "VPN", titleFont, p, Theme.Green, f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Theme.Border, 2))
            using (GraphicsPath shape = Theme.Round(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Theme.S(12)))
                g.DrawPath(pen, shape);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ShowMessage) { ShowFromTray(); return; }
            if (m.Msg == QuitMessage) { ExitApp(); return; }
            base.WndProc(ref m);
            if (m.Msg == 0x84 && (int)m.Result == 1)   // WM_NCHITTEST: перетаскивание за шапку
            {
                int lp = unchecked((int)(long)m.LParam);
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (!compact && p.Y < Theme.S(44)) m.Result = (IntPtr)2;
            }
        }

        void HideToTray()
        {
            Hide();
            if (!settings.TrayHintShown)
            {
                settings.TrayHintShown = true;
                settings.Save();
                Balloon(4000, "AngardVPN работает в фоне", "Значок находится рядом с часами. Выход — через его меню.", ToolTipIcon.Info);
            }
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            Cleanup();
            base.OnFormClosing(e);
        }

        public void ExitApp()
        {
            exiting = true;
            Close();
        }

        /// Останавливает туннель и снимает все правила. Вызывается при выходе и завершении работы Windows.
        void Cleanup()
        {
            gen++;
            tick.Stop();
            tray.Visible = false;
            OpenVpnEngine e = engine;
            engine = null;
            lock (opLock)
            {
                if (e != null) e.Stop(); StopSing();
                KillSwitch.Remove();
            }
            WriteStateFile("exit");
        }

        void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // ---------- отчёт ----------

        string reportLink;      // путь к отчёту, пока строка под статусом работает как ссылка на него

        string SaveReport(string reason)
        {
            ServerRow r = list.SelectedRow;
            List<string> others = Tunnels.Active();
            int alive = 0;
            foreach (VpnServer s in servers) if (s.Latency >= 0) alive++;
            string[] facts = {
                "Версия AngardVPN:   " + Version,
                "Windows:            " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " x64" : " x86"),
                "OpenVPN:            " + (exe ?? "не найден"),
                "Брандмауэр Windows: " + (KillSwitch.FirewallEnabled() ? "включён" : "ВЫКЛЮЧЕН"),
                "Блокировка при обрыве: " + (settings.KillSwitch ? "да" : "нет"),
                "Правила блокировки сейчас: " + (KillSwitch.IsActive() ? "есть" : "нет"),
                "Другие активные VPN: " + (others.Count == 0 ? "нет" : string.Join(", ", others.ToArray())),
                "Способ выдачи адреса: " + ipMethod + " (сохранённый: " + settings.IpMethod + ")",
                "Серверов в списке:  " + servers.Count + ", ответили на замер: " + alive,
                "Выбран:             " + (r == null || r.Server == null ? "Лучший доступный" : r.Server.CountryName + " " + r.Server.Ip),
                "Последний удачный:  " + (settings.LastGood == "" ? "-" : settings.LastGood),
                "Состояние:          " + state + " — " + lblStatus.Text,
                "Сервер / туннель:   " + (serverIp ?? "-") + " / " + (tunnelIp ?? "-")
            };
            return Journal.Save(Journal.Build(reason, facts));
        }

        void ManualReport()
        {
            string path = SaveReport("Создан вручную");
            if (path == null)
                MessageBox.Show(this, "Не удалось сохранить отчёт в папку «Документы».", "AngardVPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else OpenInExplorer(path);
        }

        static void OpenInExplorer(string path)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch (Exception) { }
        }

        /// Сохраняет отчёт об ошибке и превращает строку под статусом в ссылку на него.
        void AutoReport(string reason)
        {
            string path = SaveReport(reason);
            if (path == null) return;
            reportLink = path;
            lblStats.Text = "Отчёт об ошибке сохранён · открыть";
            lblStats.ForeColor = Theme.Amber;
            lblStats.Cursor = Cursors.Hand;
        }

        // ---------- состояние ----------

        void SetState(VState s, string status)
        {
            Journal.Add("Состояние: " + s + " — " + status);
            reportLink = null;
            lblStats.ForeColor = Theme.Muted;
            lblStats.Cursor = Cursors.Default;
            state = s;
            hero.State = s;
            lblStatus.Text = status;
            lblStatus.ForeColor = s == VState.On ? Theme.Green : s == VState.Blocked ? Theme.Red : Theme.Text;
            lblUnblock.Visible = s == VState.Blocked;
            if (s == VState.Off)
            {
                lblServer.Text = "Нажмите кнопку, чтобы подключиться";
                lblStats.Text = "";
            }
            else if (s == VState.Blocked)
            {
                lblServer.Text = "Туннель оборвался, трафик остановлен";
                lblStats.Text = "Нажмите кнопку, чтобы подключиться снова";
            }
            miToggle.Text = s == VState.Off || s == VState.Blocked ? "Подключить" : "Отключить";
            miToggle.Enabled = s != VState.Stopping;

            Icon ic;
            if (!icons.TryGetValue(s, out ic))
            {
                ic = MakeIcon(s);
                icons[s] = ic;
            }
            tray.Icon = ic;
            string tip = "AngardVPN — " + status;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            WriteStateFile(s.ToString());
            if (s == VState.Off || s == VState.Blocked) SetActive(null, false);
            if (s == VState.Off || s == VState.On) net.Restart();      // путь трафика сменился — замеры заново
            // Список мог не загрузиться (до подключения источники бывают недоступны): через туннель повторяем сразу.
            if (s == VState.On && !lastOk && !loading) LoadServers();
            UpdateMini();
            ApplyOnTop();
        }

        /// Значок в трее — тот же щит, окрашенный по состоянию.
        static Icon MakeIcon(VState s)
        {
            using (Bitmap b = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(b))
                    Theme.DrawLogo(g, new RectangleF(1, 0, 30, 32), s == VState.Stopping ? VState.Off : s);
                IntPtr h = b.GetHicon();
                Icon ic = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return ic;
            }
        }

        /// Состояние для диагностики: %LOCALAPPDATA%\AngardVPN\state.txt
        void WriteStateFile(string s)
        {
            try
            {
                File.WriteAllText(Path.Combine(Paths.DataDir, "state.txt"),
                    s + "|" + (serverIp ?? "") + "|" + (tunnelIp ?? "") + "|" + lblStatus.Text, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void UpdateStats()
        {
            if (state != VState.On) return;
            TimeSpan t = DateTime.Now - since;
            // Текущая скорость: сколько байт прошло через туннель за последнюю секунду.
            long i = bytesIn, o = bytesOut;
            long di = Math.Max(0, i - lastIn), dout = Math.Max(0, o - lastOut);
            lastIn = i; lastOut = o;
            rateIn = di;
            if (compact) UpdateMini();
            lblStats.Text = string.Format("{0:00}:{1:00}:{2:00}    ↓ {3}/с    ↑ {4}/с",
                (int)t.TotalHours, t.Minutes, t.Seconds, Fmt.Bytes(di), Fmt.Bytes(dout));
        }

        long lastIn, lastOut;

        // ---------- список серверов ----------

        void AddAutoRow()
        {
            ServerRow auto = new ServerRow();
            auto.Code = "";
            auto.Title = "Лучший доступный";
            auto.Sub = "По отклику и скорости";
            auto.Right1 = ""; auto.Right2 = "";
            list.Rows.Add(auto);
        }

        /// Загружает все списки заново (VPN Gate, общедоступные VLESS, подписка). after выполняется, когда всё загружено;
        /// если загрузка уже идёт, after дожидается её конца.
        void LoadServers(Action after)
        {
            if (after != null) afterLoad.Add(after);
            if (loading) return;
            loading = true;
            hero.Busy = true;
            lblSection.Text = "ЗАГРУЗКА СПИСКА…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool cached;
                System.Diagnostics.Stopwatch lw = System.Diagnostics.Stopwatch.StartNew();
                List<VpnServer> l = ServerList.Load(out cached);
                Journal.Add("Список VPN Gate: серверов " + l.Count + (cached ? " (из сохранённой копии)" : "") + " за " + (lw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " с");
                Ui(delegate
                {
                    if (l.Count == 0 && servers.Count > 0)
                    {
                        lblSection.Text = "СПИСОК НЕ ОБНОВЛЁН (НЕТ СВЯЗИ)";
                        FinishLoad(false);
                        return;
                    }
                    servers = l;
                    lock (failed) { failed.Clear(); }
                    FillList(cached);
                    Scan(null);
                    LoadExtra(l, cached);
                });
            });
        }

        void LoadServers() { LoadServers(null); }

        /// Загрузка закончена: запоминается время, обновляется подпись и выполняются отложенные действия (например, подключение).
        void FinishLoad(bool ok)
        {
            lastTry = DateTime.Now;
            lastOk = ok;
            hero.Busy = false;
            if (ok)
            {
                listAt = lastTry;
                keepScroll = true;
                FillList(listCached);
                keepScroll = false;
            }
            loading = false;
            if (!scanning) TestVlessInBackground();
            Action[] run = afterLoad.ToArray();
            afterLoad.Clear();
            foreach (Action a in run) a();
        }

        /// Серверы VLESS (общедоступные и из подписки) подгружаются следом за основным списком,
        /// чтобы основной список появился сразу.
        void LoadExtra(List<VpnServer> baseList, bool cached)
        {
            bool viaTunnel = state != VState.Off;
            Vless.Trace = delegate(string t) { Journal.Add(t); };
            Journal.Add("Загружаю серверы VLESS");
            // Отдельный поток, а не общий пул: загрузка долгая и не должна ждать очереди за другими задачами.
            Thread loader = new Thread(delegate()
            {
                Journal.Add("Загрузка серверов VLESS: поток начал работу");
                List<VpnServer> extra = new List<VpnServer>();
                if (settings.FreeServers)
                {
                    string note;
                    System.Diagnostics.Stopwatch fw = System.Diagnostics.Stopwatch.StartNew();
                    extra.AddRange(Vless.LoadFree(out note));
                    note += " за " + (fw.ElapsedMilliseconds / 1000) + " с";
                    Journal.Add("Общедоступные серверы VLESS: " + note);
                }
                if (settings.Subscription != "")
                {
                    string err;
                    List<VpnServer> v = Vless.Load(settings.Subscription, out err);
                    extra.AddRange(v);
                    Journal.Add("Подписка VLESS: серверов " + v.Count + (err != null ? ", " + err : "") + (singExe == null ? "; sing-box не найден в папке engine" : ""));
                    if (v.Count == 0 || singExe == null)
                        Ui(delegate { Balloon(6000, "Подписка VLESS", singExe == null ? "Не найден sing-box в папке engine." : "Серверы не получены: " + (err ?? "причина неизвестна"), ToolTipIcon.Warning); });
                }
                if (extra.Count > 0) ServerPicker.Measure(extra, ScanMs, viaTunnel);
                Journal.Add("Замер задержки до серверов VLESS: " + extra.Count);
                Ui(delegate
                {
                    if (servers == baseList && extra.Count > 0)
                    {
                        List<VpnServer> all = new List<VpnServer>(baseList);
                        all.AddRange(extra);
                        servers = all;
                        keepScroll = true;
                        FillList(cached);
                        keepScroll = false;
                    }
                    FinishLoad(!cached || (settings.FreeServers && Vless.LastFreeOk));
                });
            });
            loader.IsBackground = true;
            loader.Start();
        }

        const int ReloadMinutes = 15;      // как часто список обновляется сам
        const int StaleMinutes = 10;       // старше этого перед подключением он обновляется

        bool ListStale { get { return (DateTime.Now - lastTry).TotalMinutes > StaleMinutes; } }

        /// Бесплатные серверы быстро меняются, поэтому перед подключением устаревший список обновляется.
        void ConnectWithFreshList()
        {
            if (!ListStale) { StartConnect(false); return; }
            int my = ++gen;
            SetState(VState.Connecting, "Обновляю список серверов…");
            lblServer.Text = "";
            lblStats.Text = "";
            LoadServers(delegate { if (gen == my) StartConnect(false); });
        }

        /// Замеряет задержку до серверов в фоне. При работающем туннеле или блокировке замер был бы ложным — пропускается.
        void Scan(Action then)
        {
            if ((state != VState.Off && state != VState.On) || scanning || servers.Count == 0)
            {
                if (then != null) then();
                return;
            }
            scanning = true;
            List<VpnServer> snapshot = servers;
            bool viaTunnel = state == VState.On;      // через туннель обновляется только доступность
            ThreadPool.QueueUserWorkItem(delegate
            {
                ServerPicker.Measure(snapshot, ScanMs, viaTunnel);
                scanning = false;
                Ui(delegate
                {
                    if (snapshot == servers) { if (list.Rows.Count > 1) RefreshRows(); else FillList(false); }
                    if (then != null) then();
                    if (snapshot == servers && !loading) TestVlessInBackground();
                });
            });
        }

        bool keepScroll;     // обновление данных в открытом списке не должно сбивать прокрутку

        /// Список по странам: одна строка на страну, в ней лучший из отвечающих серверов (отклик, затем скорость).
        /// Остальные серверы страны не показываются, но участвуют в переборе при подключении к ней.
        void FillList(bool cached)
        {
            listCached = cached;
            Dictionary<string, List<VpnServer>> byCountry = new Dictionary<string, List<VpnServer>>();
            int alive = 0;
            foreach (VpnServer s in servers)
            {
                List<VpnServer> g;
                if (!byCountry.TryGetValue(s.CountryShort, out g)) { g = new List<VpnServer>(); byCountry[s.CountryShort] = g; }
                g.Add(s);
                if (s.Latency >= 0) alive++;
            }
            List<ServerRow> rows = new List<ServerRow>();
            foreach (KeyValuePair<string, List<VpnServer>> p in byCountry)
            {
                List<VpnServer> g = ServerPicker.Order(p.Value, null, null, "");
                VpnServer best = g[0];
                int up = 0;
                foreach (VpnServer s in g) if (s.Latency >= 0) up++;
                bool here = active != null && active.CountryShort == p.Key;
                ServerRow r = new ServerRow();
                r.Server = here ? active : best;
                r.Code = p.Key;
                r.Title = best.CountryName;
                if (here)
                {
                    r.Pinned = true;
                    r.Sub = active.Ip + (activeOn ? " · подключено" : " · подключение…");
                    r.SubColor = activeOn ? Theme.Green : Theme.Amber;
                }
                else if (up == 0 && best.Latency == -1) { r.Sub = "не отвечает"; r.SubColor = Theme.Red; }
                else r.Sub = "серверов: " + g.Count + (up > 0 && up < g.Count ? ", отвечают " + up : "");
                r.Right1 = r.Server.SpeedText;
                r.Right2 = Lat(r.Server);
                r.Right2Color = LatencyColor(r.Server);
                rows.Add(r);
            }
            // Страна текущего подключения наверху, остальные — от быстрого отклика к медленному.
            rows.Sort(delegate(ServerRow a, ServerRow b)
            {
                int c = (b.Pinned ? 1 : 0).CompareTo(a.Pinned ? 1 : 0);
                if (c != 0) return c;
                c = LatencyKey(a.Server).CompareTo(LatencyKey(b.Server));
                return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
            });
            list.Rows.Clear();
            AddAutoRow();
            int sel = 0;
            foreach (ServerRow r in rows)
            {
                if (r.Code == settings.Selected) sel = list.Rows.Count;
                list.Rows.Add(r);
            }
            list.SelectedIndex = sel;
            list.Reset();
            if (!keepScroll) { if (active != null) list.ScrollTop(); else list.EnsureVisible(); }
            lblSection.Text = servers.Count == 0 ? "СПИСОК СЕРВЕРОВ НЕДОСТУПЕН"
                : !lastOk ? "СПИСОК ОТ " + (listAt == DateTime.MinValue ? "?" : listAt.ToString("HH:mm")) + " · НЕТ СВЯЗИ С ИСТОЧНИКОМ"
                : byCountry.Count + " СТРАН · ОТВЕЧАЮТ " + alive + " ИЗ " + servers.Count + (listAt == DateTime.MinValue ? "" : " · " + listAt.ToString("HH:mm")) + (cached ? " (КЭШ)" : "");
            lblSection.ForeColor = lastOk ? Theme.Muted : Theme.Amber;
            UpdateBar();
        }

        /// Показывает сервер наверху списка: при подключении (connected == false) и после него. null — убрать.
        void SetActive(VpnServer srv, bool connected)
        {
            if (active == srv && activeOn == connected) return;
            active = srv;
            activeOn = connected;
            FillList(listCached);
        }

        static int LatencyKey(VpnServer s)
        {
            return s.Latency >= 0 ? s.Latency : s.Latency == -2 ? 100000 : 200000;
        }

        // ---------- подключение ----------

        void OnPowerClick()
        {
            switch (state)
            {
                case VState.Off:
                    List<string> others = Tunnels.Active();
                    if (others.Count > 0 && MessageBox.Show(this,
                        "Сейчас активен другой VPN: " + string.Join(", ", others.ToArray()) + ".\n\n" +
                        "Два VPN одновременно мешают друг другу: трафик идёт «туннель в туннеле», скорость падает в разы, " +
                        "а часть серверов не подключается. Другой VPN к тому же может закрывать доступ к спискам серверов.\n\n" +
                        "Выключите его и нажмите «Нет», затем подключитесь снова.\n\nВсё равно подключиться?",
                        "AngardVPN", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                        return;
                    ConnectWithFreshList();
                    break;
                case VState.Blocked:
                    StartConnect(true);
                    break;
                case VState.Connecting:
                case VState.On:
                    Disconnect();
                    break;
            }
        }

        /// Щелчок по серверу в списке: подключиться к нему, а если соединение уже есть — переключиться.
        void OnServerClicked()
        {
            ServerRow row = list.SelectedRow;
            if (row == null || state == VState.Stopping) return;
            // Уже в этой стране, либо выбран «Лучший доступный» при живом соединении — менять нечего.
            if (state == VState.On && (row.Server == null || (active != null && row.Code == active.CountryShort))) return;
            if (state == VState.Off) OnPowerClick();
            else if (state == VState.Blocked) StartConnect(true);
            else SwitchServer();
        }

        /// Останавливает текущий туннель (или текущую попытку) и подключается к выбранному серверу.
        void SwitchServer()
        {
            int my = ++gen;
            OpenVpnEngine eng = engine;
            engine = null;
            SetState(VState.Connecting, "Переключение…");
            SetActive(null, false);
            lblServer.Text = "";
            lblStats.Text = "";
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (opLock) { if (eng != null) eng.Stop(); StopSing(); }
                Ui(delegate { if (gen == my) ConnectWithFreshList(); });
            });
        }

        void StartConnect(bool keepBlockedOnFailure)
        {
            if (exe == null)
            {
                MessageBox.Show(this, "Не найден OpenVPN. Установите OpenVPN Community с сайта openvpn.net и запустите AngardVPN снова.",
                    "AngardVPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (servers.Count == 0)
            {
                if (state != VState.Blocked) SetState(VState.Off, "Список серверов пуст");
                lblServer.Text = "Нажмите «Обновить» и попробуйте снова";
                return;
            }
            bool ks = settings.KillSwitch;
            if (ks && !KillSwitch.FirewallEnabled())
            {
                ks = false;
                Balloon(5000, "Защита от утечек недоступна", "Брандмауэр Windows выключен, поэтому блокировка при обрыве не сработает.", ToolTipIcon.Warning);
            }
            ServerRow row = list.SelectedRow;
            VpnServer selected = row == null ? null : row.Server;
            List<VpnServer> all = servers;
            attemptNo = 0;
            int my = ++gen;
            bytesIn = 0; bytesOut = 0;
            SetState(VState.Connecting, "Подключение…");
            SetActive(null, false);
            lblServer.Text = "";
            lblStats.Text = "";
            Thread t = new Thread(delegate() { ConnectWorker(my, all, selected, ks, keepBlockedOnFailure); });
            t.IsBackground = true;
            t.Start();
        }

        static string StageText(string stage)
        {
            switch (stage)
            {
                case "AUTH": return "обмен ключами";
                case "GET_CONFIG": return "получение настроек";
                case "ASSIGN_IP":
                case "ADD_ROUTES": return "настройка адаптера";
                case "CONNECTED": return "проверка интернета";
                default: return "соединение";
            }
        }

        enum Try { Ok, Failed, Adapter, Cancelled }

        // ---------- VLESS через sing-box ----------

        const int VlessGroup = 8;           // сколько самых быстрых по проверке серверов отдаётся sing-box на автовыбор
        const int TestOnConnect = 96;       // сколько непроверенных серверов проверяется перед подключением
        const int TestInBackground = 200;   // и в фоне после загрузки списка
        volatile bool testing;              // идёт фоновая проверка скорости VLESS

        /// Фоновая проверка скорости всех серверов VLESS, пока VPN выключен (во время работы туннеля замер был бы ложным).
        void TestVlessInBackground()
        {
            if (singExe == null || testing || state != VState.Off || KillSwitch.IsBlocking()) return;
            List<VpnServer> snapshot = servers, vl = new List<VpnServer>();
            foreach (VpnServer s in ServerPicker.Order(snapshot, null, null, "")) if (s.Vless != null && s.Latency != -1 && !s.TestedFresh) vl.Add(s);
            if (vl.Count == 0) return;
            vl = ServerPicker.Diverse(vl, TestInBackground, 25);
            testing = true;
            List<string> others = Tunnels.Active();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string note;
                try
                {
                    VlessTester.Run(singExe, vl, others.Count > 0 ? Tunnels.PhysicalInterface() : null, 150000, 12,
                        delegate { return state == VState.Off; },     // при подключении фоновая проверка уступает место
                        delegate(int d, int t) { if (d % 10 == 0 || d == t) Ui(delegate { if (snapshot == servers && !loading) lblSection.Text = "ПРОВЕРКА СКОРОСТИ VLESS: " + d + " ИЗ " + t; }); },
                        out note);
                    Journal.Add("Фоновая проверка скорости VLESS: " + note);
                }
                finally { testing = false; }
                Ui(delegate { if (snapshot == servers) { keepScroll = true; FillList(listCached); keepScroll = false; } });
            });
        }

        /// Если фоновая проверка уже идёт, подключение ждёт её: два замера сразу мешали бы друг другу.
        void WaitBackgroundTest(int my)
        {
            if (!testing) return;
            Progress(my, "Проверяю скорость серверов", "Заканчиваю начатую проверку");
            System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
            while (testing && gen == my && w.ElapsedMilliseconds < 180000) Thread.Sleep(200);
        }
        readonly string singExe = SingBox.FindExe();
        volatile SingBox sing;

        void StopSing()
        {
            SingBox s = sing;
            sing = null;
            if (s != null) s.Stop();
        }

        static VpnServer CurrentVless(List<VpnServer> group, int clashPort)
        {
            string tag = SingBox.Now(clashPort);
            int i;
            if (tag != null && tag.Length > 1 && int.TryParse(tag.Substring(1), out i) && i >= 0 && i < group.Count) return group[i];
            return null;
        }

        /// Подключение по подписке VLESS: все подходящие серверы отдаются sing-box одной группой,
        /// и он сам выбирает самый быстрый по URL-тесту (и переключается, если тот упадёт).
        Try TryVless(int my, List<VpnServer> pool, bool ks)
        {
            int num = Interlocked.Increment(ref attemptNo);
            List<VpnServer> use = new List<VpnServer>(pool.Count > VlessGroup ? pool.GetRange(0, VlessGroup) : pool);
            int clash = OpenVpnEngine.FreePort();
            // Сервер, которого ядро не примет, убирается заранее: иначе не запустилась бы вся группа.
            for (int r = 0; r < 10 && use.Count > 1; r++)
            {
                string err;
                try { File.WriteAllText(Paths.SingConfig, Vless.Config(use, clash, true, 0, null), new UTF8Encoding(false)); }
                catch (IOException) { break; }
                int bad = SingBox.BadOutbound(singExe, Paths.SingConfig, out err);
                if (bad < 0 || bad >= use.Count) break;
                Journal.Add("VLESS: ядро не приняло сервер " + use[bad].Vless.Host + " (" + err + ")");
                use.RemoveAt(bad);
            }
            string title = "VLESS · серверов в группе: " + use.Count;
            Progress(my, title, "Запускаю туннель");
            List<string> ips = new List<string>();
            foreach (VpnServer s in use) if (!string.IsNullOrEmpty(s.Ip)) ips.Add(s.Ip);
            SingBox sb = new SingBox();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            bool started;
            lock (opLock)
            {
                if (gen != my) return Try.Cancelled;
                if (ks) KillSwitch.Apply(ips, null);
                KillSwitch.BlockV6();
                // При чужом VPN связь с серверами идёт напрямую через сетевую карту: иначе туннель в туннеле и скорость падает в разы.
                string bind = Tunnels.Active().Count > 0 ? Tunnels.PhysicalInterface() : null;
                if (bind != null) Journal.Add("VLESS: активен другой VPN, связь с серверами — напрямую через «" + bind + "»");
                try { File.WriteAllText(Paths.SingConfig, Vless.Config(use, clash, true, 0, bind), new UTF8Encoding(false)); }
                catch (IOException) { return Try.Failed; }
                sing = sb;
                serverIp = null;
                tunnelIp = null;
                started = sb.Start(singExe, Paths.SingConfig);
            }

            string why = started ? null : "sing-box не запустился";
            for (int tunTry = 0; ; tunTry++)
            {
                System.Diagnostics.Stopwatch up = System.Diagnostics.Stopwatch.StartNew();
                while (why == null && !sb.Started && gen == my)
                {
                    if (sb.HasExited) why = "sing-box завершился при запуске";
                    else if (up.ElapsedMilliseconds > 20000) why = "sing-box не поднял туннель за 20 с";
                    else Thread.Sleep(50);
                }
                if (gen != my) { sb.Stop(); return Try.Cancelled; }
                if (why == null || tunTry >= 2) break;
                // Адаптер с тем же именем остался от прошлого запуска (аварийное завершение, быстрое переподключение):
                // Windows не даёт создать его снова. Тогда туннель поднимается под другим именем.
                sb.Stop();
                string slog = SingBox.ReadLog() ?? "";
                if (slog.IndexOf("create adapter", StringComparison.Ordinal) < 0 && slog.IndexOf("open interface", StringComparison.Ordinal) < 0) break;
                string old = Vless.TunName;
                Vless.TunName = "AngardTun" + new Random().Next(10, 99);
                Journal.Add("VLESS: адаптер «" + old + "» занят остатком прошлого запуска, пробую имя «" + Vless.TunName + "»");
                lock (opLock)
                {
                    if (gen != my) return Try.Cancelled;
                    string bind2 = Tunnels.Active().Count > 0 ? Tunnels.PhysicalInterface() : null;
                    try { File.WriteAllText(Paths.SingConfig, Vless.Config(use, clash, true, 0, bind2), new UTF8Encoding(false)); }
                    catch (IOException) { return Try.Failed; }
                    sb = new SingBox();
                    sing = sb;
                    why = sb.Start(singExe, Paths.SingConfig) ? null : "sing-box не запустился";
                }
            }

            if (why == null)
            {
                Progress(my, title, "Проверка интернета");
                lock (opLock)
                {
                    if (gen != my) return Try.Cancelled;
                    tunnelIp = Vless.TunAddress;
                    if (ks && !KillSwitch.Apply(ips, Vless.TunAddress)) KillSwitch.RemoveV4();
                }
                string net;
                bool works = NetCheck.Works(15000, delegate { return gen == my && !sb.HasExited; }, out net);
                if (gen != my) { sb.Stop(); return Try.Cancelled; }
                if (!works) why = sb.HasExited ? "sing-box завершился" : "туннель поднят, но интернет через него не идёт (" + net + ")";
                else
                {
                    VpnServer cur = CurrentVless(use, clash) ?? use[0];
                    Journal.AddAttempt("Попытка " + num + ": VLESS — ПОДКЛЮЧЕНО за " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") +
                        " с, серверов в группе " + use.Count + ", выбран " + cur.CountryShort + " " + cur.Vless.Host + ", проверка интернета: " + net, SingBox.ReadLog());
                    Ui(delegate
                    {
                        if (gen != my) return;
                        since = DateTime.Now;
                        lastIn = 0; lastOut = 0;
                        SetState(VState.On, "Защищено");
                        lblServer.Text = cur.CountryName + " · " + cur.Source;
                        SetActive(cur, true);
                        UpdateStats();
                    });
                    Thread w = new Thread(delegate() { WatchVless(my, sb, use, clash, cur); });
                    w.IsBackground = true;
                    w.Start();
                    return Try.Ok;
                }
            }

            sb.Stop();
            if (sing == sb) sing = null;
            Journal.AddAttempt("Попытка " + num + ": VLESS — " + why + " (" + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " с)", SingBox.ReadLog());
            return Try.Failed;
        }

        /// Пока соединение VLESS живо: считает трафик, показывает, на какой сервер переключился автовыбор,
        /// и замечает падение sing-box.
        void WatchVless(int my, SingBox sb, List<VpnServer> group, int clash, VpnServer shown)
        {
            int tick = 0;
            while (gen == my)
            {
                Thread.Sleep(1000);
                if (gen != my) return;
                if (sb.HasExited)
                {
                    Ui(delegate
                    {
                        if (sing != sb || state != VState.On) return;
                        sing = null;
                        gen++;
                        Balloon(4000, "Соединение потеряно", settings.KillSwitch ? "Интернет заблокирован, переподключаюсь…" : "Переподключаюсь…", ToolTipIcon.Warning);
                        StartConnect(true);
                    });
                    return;
                }
                long down, up;
                if (SingBox.Traffic(clash, out down, out up)) { bytesIn = down; bytesOut = up; }
                if (++tick % 5 != 0) continue;
                VpnServer cur = CurrentVless(group, clash);
                if (cur == null || cur == shown) continue;
                shown = cur;
                Journal.Add("VLESS: автовыбор переключился на " + cur.CountryShort + " " + cur.Vless.Host);
                Ui(delegate
                {
                    if (gen != my || state != VState.On) return;
                    lblServer.Text = cur.CountryName + " · " + cur.Source;
                    SetActive(cur, true);
                });
            }
        }



        void Progress(int my, string server, string detail)
        {
            Ui(delegate
            {
                if (gen != my || state != VState.Connecting) return;
                lblServer.Text = server;
                lblStats.Text = detail;
            });
        }

        /// Поиск рабочего сервера: свежий замер задержки, затем «гонки» по RaceSize серверов;
        /// настоящее подключение идёт только к тем, кто в гонке ответил.
        void ConnectWorker(int my, List<VpnServer> all, VpnServer selected, bool ks, bool keepBlockedOnFailure)
        {
            string summary = null;
            List<string> others = Tunnels.Active();
            bool trust = others.Count == 0;          // через чужой VPN замер задержки ничего не значит
            // При действующей блокировке (обрыв или переключение с защитой) серверы недоступны, замер был бы ложным.
            if (trust && !KillSwitch.IsBlocking())
            {
                Progress(my, "Замеряю задержку до серверов", "");
                ServerPicker.Measure(all, ScanMs);
                Ui(delegate { if (all == servers) FillList(false); });
            }
            if (gen != my) return;

            string home = Countries.Home();
            // Серверы из подписки VLESS пробуются первыми: у них свой движок и встроенный автовыбор.
            if (singExe != null)
            {
                List<VpnServer> vl = new List<VpnServer>();
                foreach (VpnServer s in ServerPicker.Order(all, selected, null, "", trust, home))
                    if (s.Vless != null && s.Latency != -1) vl.Add(s);
                // Настоящая проверка скорости через каждый сервер. Без неё выбирались прокладки на Cloudflare:
                // отклик 20 мс, а скорость 0.1–2 Мбит/с.
                WaitBackgroundTest(my);
                List<VpnServer> untested = vl.FindAll(delegate(VpnServer s) { return !s.TestedFresh; });
                // Если годные уже есть по свежей проверке, подключаемся сразу, без нового замера.
                if (selected != null) untested.RemoveAll(delegate(VpnServer s) { return s.CountryShort != selected.CountryShort; });
                int goodNow = VlessTester.Good(vl).FindAll(delegate(VpnServer s) { return selected == null || s.CountryShort == selected.CountryShort; }).Count;
                if (untested.Count > 0 && goodNow < 3 &&!KillSwitch.IsBlocking() && gen == my)
                {
                    untested = ServerPicker.Diverse(untested, TestOnConnect, selected == null ? 12 : int.MaxValue);
                    Progress(my, "Проверяю скорость серверов", "0 из " + untested.Count);
                    string note;
                    VlessTester.Run(singExe, untested, others.Count > 0 ? Tunnels.PhysicalInterface() : null, 40000, 4,
                        delegate { return gen == my; },
                        delegate(int d, int t) { Progress(my, "Проверяю скорость серверов", "Проверено " + d + " из " + t); }, out note);
                    Journal.Add("Проверка скорости VLESS перед подключением: " + note);
                    Ui(delegate { if (all == servers) { keepScroll = true; FillList(listCached); keepScroll = false; } });
                }
                if (gen != my) return;
                List<VpnServer> good = VlessTester.Good(vl);
                // Выход в своей же стране не обходит местные блокировки: такие серверы — только если других нет.
                if (selected == null && home != null)
                {
                    List<VpnServer> abroad = good.FindAll(delegate(VpnServer s) { return s.CountryShort != home; });
                    if (abroad.Count > 0 && abroad.Count < good.Count) { Journal.Add("VLESS: серверы с выходом в своей стране (" + home + ") не берутся: " + (good.Count - abroad.Count)); good = abroad; }
                }
                // Выбрана страна: сначала её годные серверы; если там годных нет — лучшие из остальных.
                if (selected != null && good.Count > 0)
                {
                    List<VpnServer> mine = good.FindAll(delegate(VpnServer s) { return s.CountryShort == selected.CountryShort; });
                    // Двух и больше хватает для автовыбора внутри страны; один — с запасными из других стран.
                    if (mine.Count == 1) foreach (VpnServer s in good) if (!mine.Contains(s)) mine.Add(s);
                    if (mine.Count > 0) good = mine;
                    else Journal.Add("VLESS: в выбранной стране (" + selected.CountryShort + ") нет годных по скорости серверов, беру лучшие из других стран");
                }
                Journal.Add("VLESS: годных по проверке скорости " + good.Count + " из " + vl.Count + (good.Count > 0 ? ", лучшие: " + SpeedHead(good, 6) : ""));
                if (good.Count > 0)
                {
                    if (TryVless(my, VlessTester.Group(good, VlessGroup), ks) != Try.Failed) return;
                    if (gen != my) return;
                    Journal.Add("VLESS не подключился, перехожу к серверам OpenVPN");
                }
                else if (vl.Count > 0) Journal.Add("VLESS: нет серверов быстрее " + VlessTester.MinMbps + " Мбит/с, перехожу к серверам OpenVPN");
            }
            HashSet<string> bad;
            lock (failed) { bad = new HashSet<string>(failed); }
            List<VpnServer> queue = ServerPicker.Order(all, selected, bad, settings.LastGood, trust, home);
            queue.RemoveAll(delegate(VpnServer s) { return s.Vless != null; });
            int total = Math.Min(queue.Count, MaxServers);
            Journal.Add("Порядок перебора: " + (trust ? "по задержке" : "по близости к " + (home ?? "?") + " (активен другой VPN: " + string.Join(", ", others.ToArray()) + ")") +
                ", серверов " + total + ", первые: " + Head(queue, 6));

            // Выбранный вручную сервер пробуем сразу, без гонки.
            List<VpnServer> direct = new List<VpnServer>();
            if (total > 0 && selected != null) direct.Add(queue[0]);
            int pos = direct.Count, adapterFails = 0;

            while (gen == my && summary == null)
            {
                List<VpnServer> winners;
                if (direct.Count > 0) { winners = direct; direct = new List<VpnServer>(); }
                else
                {
                    if (pos >= total) break;
                    List<VpnServer> wave = queue.GetRange(pos, Math.Min(RaceSize, total - pos));
                    Progress(my, "Ищу рабочий сервер", "Проверено " + pos + " из " + total);
                    pos += wave.Count;
                    lock (opLock)
                    {
                        if (gen != my) return;
                        if (ks)
                        {
                            List<string> ips = new List<string>();
                            foreach (VpnServer w in wave) ips.Add(w.RemoteIp);
                            KillSwitch.Apply(ips, null);
                        }
                        KillSwitch.BlockV6();
                    }
                    string note;
                    winners = Race.Run(wave, exe, RaceMs, RaceGraceMs, delegate { return gen == my; }, out note);
                    Journal.Add("Гонка: " + note);
                    lock (failed) { foreach (VpnServer w in wave) if (!winners.Contains(w)) failed.Add(w.HostName); }
                }

                foreach (VpnServer w in winners)
                {
                    if (gen != my) return;
                    Try r;
                    do { r = TryServer(my, w, ks); }
                    while (r == Try.Adapter && ++adapterFails < ConfigBuilder.IpMethods.Length);
                    if (r == Try.Ok || r == Try.Cancelled) return;
                    if (r == Try.Adapter)
                    {
                        // Перепробованы все способы: дело не в сервере, а в этом компьютере. Дальше перебирать бессмысленно.
                        summary = "Адаптер туннеля не получает адрес ни одним из способов (netsh, ipapi, dynamic)";
                        break;
                    }
                    lock (failed) { failed.Add(w.HostName); }
                }
            }
            if (gen != my) return;

            Ui(delegate
            {
                if (gen != my) return;
                engine = null;
                if (ks && keepBlockedOnFailure)
                {
                    SetState(VState.Blocked, "Интернет заблокирован");
                    AutoReport("Не удалось переподключиться после обрыва, интернет заблокирован");
                    return;
                }
                int g2 = ++gen;
                SetState(VState.Stopping, "Не удалось подключиться");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    lock (opLock) { KillSwitch.Remove(); }
                    Ui(delegate
                    {
                        if (gen != g2) return;
                        SetState(VState.Off, "Не удалось подключиться");
                        List<string> now = Tunnels.Active();
                        lblServer.Text = now.Count > 0 ? "Мешает другой VPN: " + string.Join(", ", now.ToArray())
                            : summary != null ? "Сетевой адаптер туннеля не настраивается"
                            : "Выберите другой сервер или обновите список";
                        AutoReport(summary ?? "Не удалось подключиться: проверено серверов — " + total);
                    });
                });
            });
        }

        static string SpeedHead(List<VpnServer> l, int n)
        {
            List<string> p = new List<string>();
            for (int i = 0; i < Math.Min(n, l.Count); i++) p.Add(l[i].CountryShort + " " + l[i].SpeedText);
            return string.Join(", ", p.ToArray());
        }

        static string Head(List<VpnServer> l, int n)
        {
            List<string> p = new List<string>();
            for (int i = 0; i < Math.Min(n, l.Count); i++) p.Add(l[i].CountryShort + " " + l[i].LatencyText);
            return string.Join(", ", p.ToArray());
        }

        /// Настоящее подключение к одному серверу: туннель, маршруты, проверка интернета.
        Try TryServer(int my, VpnServer srv, bool ks)
        {
            string cfg;
            try { cfg = srv.DecodeConfig(); }
            catch (FormatException) { return Try.Failed; }
            string ip = srv.RemoteIp;
            string method = ipMethod;
            int num = Interlocked.Increment(ref attemptNo);
            string place = srv.CountryName + " · " + ip;
            Progress(my, place, "Подключаюсь · соединение");
            Ui(delegate { if (gen == my && state == VState.Connecting) SetActive(srv, false); });

            Attempt a = new Attempt();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            OpenVpnEngine eng = new OpenVpnEngine();
            eng.StateChanged += delegate(OpenVpnEngine e, string name, string tip)
            {
                a.Stage = name;
                if (name == "AUTH" || name == "GET_CONFIG") a.Auth = true;
                else if (name == "ASSIGN_IP") { a.Auth = true; a.AssignedAt = sw.ElapsedMilliseconds; a.Assigned = true; }
                else if (name == "CONNECTED") { a.TunnelIp = tip; a.Connected = true; }
                else if (name == "RECONNECTING" || name == "EXITING")
                {
                    a.Ended = true;
                    if (a.Connected) OnDropped(e);
                    return;
                }
                Progress(my, place, "Подключаюсь · " + StageText(name));
            };
            eng.BytesChanged += delegate(OpenVpnEngine e, long bi, long bo) { bytesIn = bi; bytesOut = bo; };
            eng.Exited += delegate(OpenVpnEngine e)
            {
                a.Ended = true;
                if (a.Connected) OnDropped(e);
            };

            bool started;
            lock (opLock)
            {
                if (gen != my) return Try.Cancelled;
                if (ks) KillSwitch.Apply(ip, null);
                KillSwitch.BlockV6();
                int port = OpenVpnEngine.FreePort();
                try { File.WriteAllText(Paths.Config, ConfigBuilder.Build(cfg, port, method), new UTF8Encoding(false)); }
                catch (IOException) { return Try.Failed; }
                engine = eng;
                serverIp = ip;
                tunnelIp = null;
                started = eng.Start(exe, Paths.Config, port);
            }

            // Ждём по шагам: кто не уложился в свой срок — сразу следующий сервер.
            string why = started ? null : "OpenVPN не запустился";
            while (why == null && !a.Connected && gen == my)
            {
                long ms = sw.ElapsedMilliseconds;
                if (a.Ended) why = a.Auth ? "сервер отказал (перегружен)" : "сервер разорвал соединение";
                else if (!a.Auth && ms > HandshakeMs) why = "сервер не отвечает";
                else if (!a.Assigned && ms > ConfigMs) why = "сервер не прислал настройки";
                else if (a.Assigned && ms - a.AssignedAt > AdapterMs) why = "адаптер туннеля не получил адрес (способ " + method + ")";
                else Thread.Sleep(50);
            }
            if (gen != my) { eng.Stop(); return Try.Cancelled; }

            bool adapterProblem = why != null && a.Assigned && !a.Ended;
            if (why == null)
            {
                string tip = a.TunnelIp;
                bool warn = false;
                lock (opLock)
                {
                    if (gen != my) return Try.Cancelled;
                    tunnelIp = tip;
                    if (ks && !string.IsNullOrEmpty(tip) && !KillSwitch.Apply(ip, tip))
                    {
                        KillSwitch.RemoveV4();   // иначе интернет останется заблокированным при живом туннеле
                        warn = true;
                    }
                }
                string net;
                bool works = NetCheck.Works(NetCheckMs, delegate { return gen == my && !a.Ended; }, out net);
                if (gen != my) { eng.Stop(); return Try.Cancelled; }
                if (!works) why = a.Ended ? "туннель оборвался сразу после подключения" : "туннель поднят, но интернет через него не идёт (" + net + ")";
                else
                {
                    string took = (sw.ElapsedMilliseconds / 1000.0).ToString("0.0");
                    Journal.AddAttempt("Попытка " + num + ": " + srv.CountryShort + " " + ip + " — ПОДКЛЮЧЕНО за " + took +
                        " с, адрес туннеля " + tip + ", способ " + method + ", проверка интернета: " + net, Journal.ReadLog());
                    Ui(delegate
                    {
                        if (gen != my) return;
                        since = DateTime.Now;
                        settings.IpMethod = method;
                        settings.LastGood = srv.HostName;
                        settings.Save();
                        SetState(VState.On, "Защищено");
                        SetActive(srv, true);
                        lblServer.Text = place;
                        UpdateStats();
                        if (warn) Balloon(5000, "AngardVPN", "Не удалось включить блокировку при обрыве.", ToolTipIcon.Warning);
                        // Обрыв мог случиться, пока шла проверка интернета: тогда он был пропущен, обрабатываем сейчас.
                        if (a.Ended) OnDropped(eng);
                    });
                    return Try.Ok;
                }
            }

            a.Connected = false;        // чтобы остановка движка не считалась обрывом
            eng.Stop();
            Journal.AddAttempt("Попытка " + num + ": " + srv.CountryShort + " " + ip + " — " + why +
                " (" + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " с)", Journal.ReadLog());
            if (!adapterProblem) return Try.Failed;
            // Сервер исправен, не сработал способ выдачи адреса: следующий вызов попробует другой способ.
            ipMethod = ConfigBuilder.NextIpMethod(method);
            Journal.Add("Способ выдачи адреса изменён: " + method + " → " + ipMethod);
            return Try.Adapter;
        }

        /// Туннель оборвался сам. Правила брандмауэра остаются, приложение пробует подключиться заново.
        void OnDropped(OpenVpnEngine eng)
        {
            Ui(delegate
            {
                if (eng != engine || state != VState.On) return;
                engine = null;
                gen++;
                ThreadPool.QueueUserWorkItem(delegate { eng.Stop(); });
                Balloon(4000, "Соединение потеряно",
                    settings.KillSwitch ? "Интернет заблокирован, переподключаюсь…" : "Переподключаюсь…", ToolTipIcon.Warning);
                StartConnect(true);
            });
        }

        void Disconnect()
        {
            if (state == VState.Stopping) return;
            int my = ++gen;
            OpenVpnEngine eng = engine;
            engine = null;
            SetState(VState.Stopping, "Отключение…");
            lblServer.Text = "";
            lblStats.Text = "";
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (opLock)
                {
                    if (eng != null) eng.Stop(); StopSing();
                    KillSwitch.Remove();
                }
                Ui(delegate
                {
                    if (gen != my) return;
                    serverIp = null; tunnelIp = null;
                    SetState(VState.Off, "Не подключено");
                    Scan(null);
                });
            });
        }

        void OnKillChanged()
        {
            if (settings.KillSwitch && !KillSwitch.FirewallEnabled())
                Balloon(5000, "Защита от утечек недоступна", "Брандмауэр Windows выключен, поэтому блокировка при обрыве не сработает.", ToolTipIcon.Warning);
            if (state == VState.Blocked && !settings.KillSwitch) { Disconnect(); return; }
            if (state != VState.On) return;
            int my = gen;
            bool on = settings.KillSwitch;
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (opLock)
                {
                    if (gen != my || string.IsNullOrEmpty(serverIp) || string.IsNullOrEmpty(tunnelIp)) return;
                    if (on) { if (!KillSwitch.Apply(serverIp, tunnelIp)) KillSwitch.RemoveV4(); }
                    else KillSwitch.RemoveV4();
                }
            });
        }
    }
}
