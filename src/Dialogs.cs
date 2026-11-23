using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace MiniVpn
{
    /// Основа для маленьких окон в стиле приложения: тёмный фон, скруглённые углы, заголовок, крестик, перетаскивание.
    class DarkDialog : Form
    {
        protected readonly Font TitleFont = new Font("Segoe UI Semibold", 10.5f);
        protected int HeaderH { get { return Theme.S(44); } }

        public DarkDialog(string title, int width, int height)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            ClientSize = new Size(Theme.S(width), Theme.S(height));
            GlyphButton close = new GlyphButton();
            close.IsClose = true;
            close.SetBounds(ClientSize.Width - Theme.S(41), 1, Theme.S(40), Theme.S(34));
            close.Click += delegate { Close(); };
            Controls.Add(close);
            using (GraphicsPath shape = Theme.Round(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Theme.S(12)))
                Region = new Region(shape);
        }

        /// Ставит окно по центру владельца, не выходя за рабочую область экрана.
        public void PlaceOver(Form owner)
        {
            Rectangle wa = Screen.FromControl(owner).WorkingArea;
            int x = owner.Left + (owner.Width - Width) / 2, y = owner.Top + (owner.Height - Height) / 2;
            Location = new Point(Math.Max(wa.Left, Math.Min(x, wa.Right - Width)), Math.Max(wa.Top, Math.Min(y, wa.Bottom - Height)));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            TextRenderer.DrawText(g, Text, TitleFont, new Point(Theme.S(16), Theme.S(12)), Theme.Text, TextFormatFlags.NoPadding);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Theme.Border, 2))
            using (GraphicsPath shape = Theme.Round(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Theme.S(12)))
                g.DrawPath(pen, shape);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x84 && (int)m.Result == 1)
            {
                int lp = unchecked((int)(long)m.LParam);
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (p.Y < HeaderH) m.Result = (IntPtr)2;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected Label Button(string text, int x, int y, int w, int h, EventHandler click)
        {
            Label b = new Label();
            b.Text = text;
            b.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.BackColor = Theme.PanelHi;
            b.ForeColor = Theme.Text;
            b.Font = Font;
            b.Cursor = Cursors.Hand;
            b.MouseEnter += delegate { b.BackColor = Color.FromArgb(0x30, 0x37, 0x42); };
            b.MouseLeave += delegate { b.BackColor = Theme.PanelHi; };
            b.Click += click;
            using (GraphicsPath p = Theme.Round(new Rectangle(0, 0, b.Width, b.Height), Theme.S(8)))
                b.Region = new Region(p);
            Controls.Add(b);
            return b;
        }
    }

    /// Окно настроек.
    class SettingsForm : DarkDialog
    {
        public SettingsForm(Settings s, Action killSwitchChanged, Action report, Action subscriptionChanged, Action onTopChanged) : base("Настройки", 340, 528)
        {
            int y = 52;
            Toggle kill = Row("Блокировать интернет при обрыве", "При обрыве трафик не идёт напрямую", ref y, s.KillSwitch);
            kill.Changed += delegate { s.KillSwitch = kill.On; s.Save(); killSwitchChanged(); };

            Toggle auto = Row("Запускать вместе с Windows", "В трее, без запроса прав при входе", ref y, AutoStart.Enabled);
            auto.Changed += delegate
            {
                if (AutoStart.Set(auto.On, Application.ExecutablePath)) return;
                auto.On = !auto.On;
                MessageBox.Show(this, "Не удалось изменить задание в планировщике Windows.", "AngardVPN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            Toggle connect = Row("Подключаться при запуске", "Искать сервер сразу при открытии", ref y, s.AutoConnect);
            connect.Changed += delegate { s.AutoConnect = connect.On; s.Save(); };

            Toggle hidden = Row("Запускать свёрнутым", "Окно не появляется, значок у часов", ref y, s.StartHidden);
            hidden.Changed += delegate { s.StartHidden = hidden.On; s.Save(); };

            Toggle notify = Row("Уведомления", "Сообщать об обрыве и переподключении", ref y, s.Notify);
            notify.Changed += delegate { s.Notify = notify.On; s.Save(); };

            Toggle top = Row("Поверх всех окон", "Пока VPN подключён, окно не прячется за другими", ref y, s.OnTop);
            top.Changed += delegate { s.OnTop = top.On; s.Save(); onTopChanged(); };

            Toggle free = Row("Общедоступные серверы VLESS", "Бесплатные, из открытого списка; много европейских", ref y, s.FreeServers);
            free.Changed += delegate { s.FreeServers = free.On; s.Save(); subscriptionChanged(); };

            // Подписка VLESS: адрес вида https://… либо сами ссылки vless://…
            Label subTitle = new Label(), subHint = new Label();
            subTitle.Text = "Подписка VLESS";
            subTitle.SetBounds(Theme.S(16), Theme.S(y), Theme.S(190), Theme.S(22));
            subTitle.ForeColor = Theme.Text;
            subTitle.BackColor = Theme.Bg;
            subTitle.Font = Font;
            subTitle.TextAlign = ContentAlignment.MiddleLeft;
            subHint.SetBounds(Theme.S(16), Theme.S(y + 23), Theme.S(200), Theme.S(16));
            subHint.ForeColor = Theme.Muted;
            subHint.BackColor = Theme.Bg;
            subHint.Font = new Font("Segoe UI", 8f);
            Action showSub = delegate { subHint.Text = s.Subscription == "" ? "Не задана" : "Задана · серверы появятся в списке"; };
            showSub();
            Controls.Add(subTitle);
            Controls.Add(subHint);
            Button("Изменить", 226, y + 4, 98, 30, delegate
            {
                using (InputForm f = new InputForm("Подписка VLESS", s.Subscription))
                {
                    f.PlaceOver(this);
                    if (f.ShowDialog(this) != DialogResult.OK || f.Value == s.Subscription) return;
                    s.Subscription = f.Value;
                    s.Save();
                    showSub();
                    subscriptionChanged();
                }
            });
            y += 52;

            Button("Создать отчёт", 16, y + 6, 150, 32, delegate { report(); });
            Label about = new Label();
            about.Text = "AngardVPN " + MainForm.Version + "\nVPN Gate и VLESS";
            about.SetBounds(Theme.S(176), Theme.S(y + 4), Theme.S(150), Theme.S(36));
            about.TextAlign = ContentAlignment.MiddleRight;
            about.ForeColor = Theme.Idle;
            about.BackColor = Theme.Bg;
            about.Font = new Font("Segoe UI", 8f);
            Controls.Add(about);
        }

        Toggle Row(string title, string hint, ref int y, bool on)
        {
            Toggle t = new Toggle();
            t.Font = Font;
            t.Text = title;
            t.On = on;
            t.SetBounds(Theme.S(16), Theme.S(y), ClientSize.Width - Theme.S(32), Theme.S(24));
            Label h = new Label();
            h.Text = hint;
            h.SetBounds(Theme.S(16), Theme.S(y + 23), ClientSize.Width - Theme.S(80), Theme.S(16));
            h.ForeColor = Theme.Muted;
            h.BackColor = Theme.Bg;
            h.Font = new Font("Segoe UI", 8f);
            h.AutoEllipsis = true;
            Controls.Add(t);
            Controls.Add(h);
            y += 52;
            return t;
        }
    }
}
