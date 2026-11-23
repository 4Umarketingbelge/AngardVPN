using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using MiniVpn;

// Снимки окна без запуска приложения и без VPN: создаёт форму за пределами экрана,
// переключает состояния и сохраняет картинки. Аргументы: <папка>.
static class UiShot
{
    const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    [STAThread]
    static int Main(string[] a)
    {
        try { return Run(a[0]); }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(a[0], "ui-error.txt"), e.ToString());
            return 1;
        }
    }

    static object Field(object o, string name) { return o.GetType().GetField(name, Priv).GetValue(o); }

    static void Pump(int n) { for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(20); } }

    static Bitmap Grab(Form f)
    {
        Pump(3);
        Bitmap b = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
        return b;
    }

    static int Run(string dir)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        MainForm f = new MainForm(false);
        f.ShowInTaskbar = false;
        f.Location = new Point(-3000, -3000);
        f.Show();
        // Ждём загрузку списка и замер задержки.
        DateTime until = DateTime.Now.AddSeconds(25);
        bool started = false;
        while (DateTime.Now < until)
        {
            Pump(2);
            int count = ((System.Collections.ICollection)Field(f, "servers")).Count;
            bool sc = (bool)Field(f, "scanning");
            if (sc) started = true;
            if (count > 0 && started && !sc) break;
        }
        Pump(10);

        MethodInfo setState = typeof(MainForm).GetMethod("SetState", Priv | BindingFlags.DeclaredOnly, null, new Type[] { typeof(VState), typeof(string) }, null);
        MethodInfo setActive = typeof(MainForm).GetMethod("SetActive", Priv);
        MethodInfo setExpanded = typeof(MainForm).GetMethod("SetExpanded", Priv);
        System.Collections.IList srv = (System.Collections.IList)Field(f, "servers");
        dynamic hero = Field(f, "hero");
        hero.Busy = false;      // снимок делается в обычном виде кнопки, а не во время загрузки

        System.Collections.Generic.List<Bitmap> shots = new System.Collections.Generic.List<Bitmap>();
        setState.Invoke(f, new object[] { VState.Off, "Не подключено" });
        shots.Add(Grab(f));

        setState.Invoke(f, new object[] { VState.Connecting, "Подключение…" });
        if (srv.Count > 5) setActive.Invoke(f, new object[] { srv[5], false });
        ((dynamic)Field(f, "lblServer")).Text = "Ищу рабочий сервер";
        ((dynamic)Field(f, "lblStats")).Text = "Проверено 8 из 48";
        hero.Clock = 3.3;
        shots.Add(Grab(f));

        typeof(MainForm).GetField("since", Priv).SetValue(f, DateTime.Now.AddMinutes(-12).AddSeconds(-34));
        setState.Invoke(f, new object[] { VState.On, "Защищено" });
        if (srv.Count > 5) setActive.Invoke(f, new object[] { srv[5], true });
        ((dynamic)Field(f, "lblServer")).Text = "Япония · 219.100.37.192";
        ((dynamic)Field(f, "lblStats")).Text = "00:12:34    ↓ 48.2 МБ    ↑ 3.1 МБ";
        hero.Clock = 11.0;
        Pump(350);
        shots.Add(Grab(f));

        setExpanded.Invoke(f, new object[] { true });
        shots.Add(Grab(f));
        setExpanded.Invoke(f, new object[] { false });

        // Строка вместо окна: подключено, с «поверх окон», и во время подключения.
        MethodInfo setCompact = typeof(MainForm).GetMethod("SetCompact", Priv);
        dynamic mini = Field(f, "mini");
        typeof(MainForm).GetField("rateIn", Priv).SetValue(f, 2470000L);
        setCompact.Invoke(f, new object[] { true });
        shots.Add(Grab(f));
        mini.Pinned = true; mini.Invalidate();
        shots.Add(Grab(f));
        mini.Pinned = false;
        setState.Invoke(f, new object[] { VState.Connecting, "Подключение…" });
        shots.Add(Grab(f));
        setState.Invoke(f, new object[] { VState.Off, "Не подключено" });
        shots.Add(Grab(f));
        setCompact.Invoke(f, new object[] { false });     // вернуть сохранённую настройку

        // «Поверх всех окон» действует только при подключении (настройка меняется в памяти, без сохранения).
        Settings st = (Settings)Field(f, "settings");
        bool keep = st.OnTop;
        st.OnTop = true;
        string top = "";
        foreach (VState v in new VState[] { VState.Off, VState.Connecting, VState.On, VState.Blocked, VState.On, VState.Off })
        {
            setState.Invoke(f, new object[] { v, v.ToString() });
            top += v + "=" + f.TopMost + " ";
        }
        st.OnTop = false;
        setState.Invoke(f, new object[] { VState.On, "On" });
        top += "| выкл, On=" + f.TopMost;
        st.OnTop = keep;
        File.WriteAllText(Path.Combine(dir, "ontop.txt"), top);

        // Окно настроек.
        using (SettingsForm s = new SettingsForm(new Settings(), delegate { }, delegate { }, delegate { }, delegate { }))
        {
            s.Location = new Point(-3000, -2000);
            s.Show();
            shots.Add(Grab(s));
            s.Close();
        }

        int w = 20, h = 0;
        foreach (Bitmap b in shots) { w += b.Width + 20; h = Math.Max(h, b.Height); }
        using (Bitmap all = new Bitmap(w, h + 40))
        {
            using (Graphics g = Graphics.FromImage(all))
            {
                g.Clear(Color.FromArgb(0x30, 0x34, 0x3C));
                int x = 20;
                foreach (Bitmap b in shots) { g.DrawImage(b, x, 20); x += b.Width + 20; }
            }
            all.Save(Path.Combine(dir, "ui-preview.png"), ImageFormat.Png);
        }
        Environment.Exit(0);    // без штатного выхода: правила брандмауэра этот тест не трогает
        return 0;
    }
}
