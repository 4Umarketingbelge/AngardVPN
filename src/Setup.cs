using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using MiniVpn;

// Установщик AngardVPN. Внутри лежат payload.zip (приложение, sing-box, лицензии) и openvpn.msi.
// Ключи: /silent — без окна; /dir=ПУТЬ — папка установки; /uninstall — удалить;
//        /purge — при удалении убрать и настройки; /noopenvpn — не ставить OpenVPN;
//        /nosys — не трогать систему (ярлыки, реестр, брандмауэр, планировщик, OpenVPN): для проверок без прав администратора.
static class SetupMain
{
    internal const string Name = "AngardVPN";
    internal static string Dir;
    internal static bool Silent, NoSys, NoOpenVpn, Uninstall, Purge;

    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "AngardVPN-setup.log");

    internal static void Log(string s)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n", Encoding.UTF8); }
        catch (IOException) { }
    }

    static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch (Exception) { return false; }
    }

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string dir = null;
        foreach (string a in args)
        {
            string l = a.ToLowerInvariant();
            if (l.StartsWith("/dir=")) dir = a.Substring(5).Trim('"');
            else if (l == "/silent" || l == "/s") Silent = true;
            else if (l == "/nosys") NoSys = true;
            else if (l == "/noopenvpn") NoOpenVpn = true;
            else if (l == "/uninstall") Uninstall = true;
            else if (l == "/purge") Purge = true;
        }
        Dir = dir ?? (Uninstall ? Path.GetDirectoryName(Application.ExecutablePath)
                                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name));
        Dir = Path.GetFullPath(Dir).TrimEnd('\\');

        // Для установки в Program Files нужны права администратора: запрашиваем их заново, с теми же ключами.
        if (!NoSys && !IsAdmin())
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", Array.ConvertAll(args, delegate(string x) { return x.IndexOf(' ') >= 0 ? "\"" + x + "\"" : x; })));
                si.UseShellExecute = true;
                si.Verb = "runas";
                Process.Start(si);
            }
            catch (Exception) { }          // отказ в окне контроля учётных записей — просто выходим
            return 0;
        }

        try
        {
            Log("запуск: " + string.Join(" ", args) + "; папка " + Dir);
            if (Uninstall) return RunUninstall();
            return RunInstall();
        }
        catch (Exception e)
        {
            Log("ОШИБКА: " + e);
            if (!Silent) MessageBox.Show("Не удалось выполнить операцию:\n\n" + e.Message + "\n\nПодробности: " + LogPath, "AngardVPN", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    // ---------- установка ----------

    static int RunInstall()
    {
        if (Silent)
        {
            Install(!NoSys, !NoOpenVpn && !NoSys && !OpenVpnInstalled(), null);
            return 0;
        }
        Application.Run(new SetupForm());
        return 0;
    }

    internal static bool OpenVpnInstalled()
    {
        return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"OpenVPN\bin\openvpn.exe"))
            || File.Exists(@"C:\Program Files\OpenVPN\bin\openvpn.exe");
    }

    static string Under(string root, string rel)
    {
        string full = Path.GetFullPath(Path.Combine(root, rel));
        string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Недопустимый путь в архиве: " + rel);
        return full;
    }

    /// Закрывает запущенный AngardVPN и его sing-box (только из папки установки: чужие sing-box не трогаются).
    static void StopRunning()
    {
        foreach (string n in new string[] { "AngardVPN", "sing-box" })
            foreach (Process p in Process.GetProcessesByName(n))
            {
                try
                {
                    string f = p.MainModule.FileName;
                    if (f.StartsWith(Dir + "\\", StringComparison.OrdinalIgnoreCase)) { Log("останавливаю " + f); p.Kill(); p.WaitForExit(8000); }
                }
                catch (Exception) { }
            }
    }

    static int Netsh(string args)
    {
        try
        {
            ProcessStartInfo si = new ProcessStartInfo("netsh.exe", args);
            si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardOutput = true;
            using (Process p = Process.Start(si)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(15000); return p.HasExited ? p.ExitCode : -1; }
        }
        catch (Exception) { return -1; }
    }

    /// Если приложение закрыли аварийно, правила блокировки могли остаться и держать интернет закрытым.
    static void RemoveRules()
    {
        foreach (string r in new string[] { "AngardVPN KillSwitch A", "AngardVPN KillSwitch B", "AngardVPN IPv6" })
            Netsh("advfirewall firewall delete rule name=\"" + r + "\"");
    }

    static void Shortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        dynamic sh = Activator.CreateInstance(t);
        dynamic l = sh.CreateShortcut(path);
        l.TargetPath = target;
        l.WorkingDirectory = Path.GetDirectoryName(target);
        l.IconLocation = target + ",0";
        l.Description = Name;
        l.Save();
    }

    static string StartMenuDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Name); } }
    static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Name + ".lnk"); } }
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AngardVPN";

    static void Report(Action<double, string> r, double f, string s)
    {
        Log(s);
        if (r != null) r(f, s);
    }

    internal static void Install(bool desktopLink, bool openVpn, Action<double, string> report)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        Report(report, 0.02, "Закрываю запущенную копию…");
        StopRunning();
        if (!NoSys) RemoveRules();
        Directory.CreateDirectory(Dir);

        long total = 0;
        using (Stream s = asm.GetManifestResourceStream("payload.zip"))
        using (ZipArchive z = new ZipArchive(s, ZipArchiveMode.Read))
        {
            int n = z.Entries.Count, i = 0;
            foreach (ZipArchiveEntry e in z.Entries)
            {
                i++;
                if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(Under(Dir, e.FullName.Replace('/', '\\'))); continue; }
                string target = Under(Dir, e.FullName.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                Report(report, 0.05 + 0.55 * i / n, "Копирую " + e.Name + "…");
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        using (Stream src = e.Open())
                        using (FileStream dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
                            src.CopyTo(dst);
                        break;
                    }
                    catch (IOException) { if (attempt >= 6) throw; Thread.Sleep(700); }
                }
                total += e.Length;
            }
        }

        // Копия установщика становится удалителем.
        try
        {
            string self = Application.ExecutablePath, un = Path.Combine(Dir, "Uninstall.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(un), StringComparison.OrdinalIgnoreCase)) File.Copy(self, un, true);
        }
        catch (IOException ex) { Log("Uninstall.exe не скопирован: " + ex.Message); }

        if (!NoSys)
        {
            Report(report, 0.65, "Создаю ярлыки…");
            string exe = Path.Combine(Dir, "AngardVPN.exe");
            Shortcut(Path.Combine(StartMenuDir, Name + ".lnk"), exe);
            Shortcut(Path.Combine(StartMenuDir, "Удалить " + Name + ".lnk"), Path.Combine(Dir, "Uninstall.exe"));
            if (desktopLink) Shortcut(DesktopLink, exe);

            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Name);
                k.SetValue("DisplayVersion", SetupVer.V);
                k.SetValue("Publisher", Name);
                k.SetValue("InstallLocation", Dir);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("UninstallString", "\"" + Path.Combine(Dir, "Uninstall.exe") + "\" /uninstall");
                k.SetValue("EstimatedSize", (int)(total / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        if (openVpn)
        {
            Report(report, 0.72, "Устанавливаю OpenVPN для серверов VPN Gate (до минуты)…");
            string tmp = Path.Combine(Path.GetTempPath(), "AngardVPN-setup");
            Directory.CreateDirectory(tmp);
            string msi = Path.Combine(tmp, "openvpn.msi");
            using (Stream s = asm.GetManifestResourceStream("openvpn.msi"))
            using (FileStream f = new FileStream(msi, FileMode.Create, FileAccess.Write)) s.CopyTo(f);
            ProcessStartInfo si = new ProcessStartInfo("msiexec.exe", "/i \"" + msi + "\" ADDLOCAL=OpenVPN,Drivers,Drivers.TAPWindows6 /qn /norestart");
            si.UseShellExecute = false; si.CreateNoWindow = true;
            using (Process p = Process.Start(si))
            {
                if (!p.WaitForExit(300000)) { try { p.Kill(); } catch (Exception) { } Log("OpenVPN: установка не уложилась в 5 минут"); }
                else Log("OpenVPN: код " + p.ExitCode);
            }
            try { File.Delete(msi); Directory.Delete(tmp); } catch (IOException) { }
        }
        Report(report, 1.0, "Готово");
    }

    // ---------- удаление ----------

    static int RunUninstall()
    {
        if (!Silent)
        {
            if (MessageBox.Show("Удалить AngardVPN с этого компьютера?", "AngardVPN", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
            Purge = MessageBox.Show("Удалить заодно настройки и журналы?\n\nОтвет «Нет» оставит их на случай повторной установки.", "AngardVPN", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        StopRunning();
        if (!NoSys)
        {
            RemoveRules();
            try
            {
                ProcessStartInfo si = new ProcessStartInfo("schtasks.exe", "/delete /tn \"AngardVPN\" /f");
                si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardOutput = true; si.RedirectStandardError = true;
                using (Process p = Process.Start(si)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(15000); }
            }
            catch (Exception) { }
            try { if (File.Exists(DesktopLink)) File.Delete(DesktopLink); } catch (IOException) { }
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch (IOException) { }
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
        }

        // Удаляются только файлы из состава пакета: чужое в папке установки не затрагивается.
        List<string> files = new List<string>();
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (ZipArchive z = new ZipArchive(s, ZipArchiveMode.Read))
            foreach (ZipArchiveEntry e in z.Entries)
                if (!e.FullName.EndsWith("/")) files.Add(Under(Dir, e.FullName.Replace('/', '\\')));
        foreach (string f in files) { try { File.Delete(f); } catch (Exception ex) { Log("не удалён " + f + ": " + ex.Message); } }
        foreach (string sub in new string[] { "engine", "licenses" })
            try { string d = Path.Combine(Dir, sub); if (Directory.Exists(d)) Directory.Delete(d); } catch (IOException) { }

        if (Purge)
            try { string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AngardVPN"); if (Directory.Exists(d)) Directory.Delete(d, true); } catch (IOException) { }

        // Сам удалитель занят работой: его и пустую папку убирает отдельная команда после выхода.
        string me = Path.Combine(Dir, "Uninstall.exe");
        if (File.Exists(me))
        {
            ProcessStartInfo si = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + me + "\" & rmdir \"" + Dir + "\"");
            si.UseShellExecute = false; si.CreateNoWindow = true;
            Process.Start(si);
        }
        Log("удалено");
        if (!Silent) MessageBox.Show("AngardVPN удалён." + (OpenVpnInstalled() ? "\nOpenVPN остался на компьютере: его можно убрать отдельно в «Установке и удалении программ»." : ""), "AngardVPN", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}

class SetupForm : Form
{
    readonly Font titleFont = new Font("Segoe UI Semibold", 16f), subFont = new Font("Segoe UI", 9f);
    readonly CheckBox cbDesk = new CheckBox(), cbVpn = new CheckBox();
    readonly Label pathCap = new Label(), pathVal = new Label(), note = new Label(), status = new Label();
    readonly Label btnMain, btnClose;
    readonly Panel barBack = new Panel(), barFill = new Panel();
    bool busy, finished, failed;

    Label Button(string text, int x, int w, EventHandler click, bool primary)
    {
        Label b = new Label();
        b.Text = text;
        b.SetBounds(Theme.S(x), Theme.S(318), Theme.S(w), Theme.S(38));
        b.TextAlign = ContentAlignment.MiddleCenter;
        b.BackColor = primary ? Theme.Green : Theme.PanelHi;
        b.ForeColor = primary ? Color.FromArgb(0x0D, 0x12, 0x17) : Theme.Text;
        b.Font = new Font("Segoe UI Semibold", 9.5f);
        b.Cursor = Cursors.Hand;
        b.Click += click;
        using (GraphicsPath p = Theme.Round(new Rectangle(0, 0, b.Width, b.Height), Theme.S(9))) b.Region = new Region(p);
        Controls.Add(b);
        return b;
    }

    void Caption(Label l, string text, int y, int h, Color c, float size)
    {
        l.Text = text;
        l.SetBounds(Theme.S(28), Theme.S(y), Theme.S(404), Theme.S(h));
        l.ForeColor = c;
        l.BackColor = Theme.Bg;
        l.Font = new Font("Segoe UI", size);
        Controls.Add(l);
    }

    void Check(CheckBox c, string text, int y, bool on)
    {
        c.Text = text;
        c.Checked = on;
        c.SetBounds(Theme.S(28), Theme.S(y), Theme.S(404), Theme.S(24));
        c.ForeColor = Theme.Text;
        c.BackColor = Theme.Bg;
        c.FlatStyle = FlatStyle.Flat;
        c.FlatAppearance.BorderColor = Theme.Idle;
        c.FlatAppearance.CheckedBackColor = Theme.Green;
        Controls.Add(c);
    }

    public SetupForm()
    {
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Theme.K = g.DpiX / 96f;
        Text = "Установка AngardVPN";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(Theme.S(460), Theme.S(376));
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        Caption(pathCap, "ПАПКА УСТАНОВКИ", 100, 16, Theme.Muted, 7.5f);
        Caption(pathVal, SetupMain.Dir, 117, 22, Theme.Text, 9.5f);
        pathVal.AutoEllipsis = true;
        Check(cbDesk, "Ярлык на рабочем столе", 156, true);
        bool have = SetupMain.OpenVpnInstalled();
        Check(cbVpn, have ? "OpenVPN уже установлен (серверы VPN Gate доступны)" : "Серверы VPN Gate: установить OpenVPN (6 МБ)", 188, true);
        cbVpn.Enabled = !have;
        Caption(note, "Серверы VLESS (европейские и другие) работают сразу, ничего дополнительно не требуется. " +
            "Для работы нужны права администратора.", 224, 56, Theme.Muted, 8.5f);

        barBack.SetBounds(Theme.S(28), Theme.S(292), Theme.S(404), Theme.S(5));
        barBack.BackColor = Theme.Border;
        barFill.SetBounds(0, 0, 0, Theme.S(5));
        barFill.BackColor = Theme.Green;
        barBack.Controls.Add(barFill);
        barBack.Visible = false;
        Controls.Add(barBack);
        Caption(status, "", 298, 18, Theme.Muted, 8.5f);

        btnClose = Button("Отмена", 28, 170, delegate { if (!busy) Close(); }, false);
        btnMain = Button("Установить", 220, 212, delegate { OnMain(); }, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.DrawLogo(g, new RectangleF(Theme.S(28), Theme.S(22), Theme.S(50), Theme.S(54)), VState.On);
        TextRenderer.DrawText(g, "Angard", titleFont, new Point(Theme.S(92), Theme.S(22)), Theme.Text, TextFormatFlags.NoPadding);
        int w = TextRenderer.MeasureText(g, "Angard", titleFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        TextRenderer.DrawText(g, "VPN", titleFont, new Point(Theme.S(92) + w, Theme.S(22)), Theme.Green, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Установка · версия " + SetupVer.V, subFont, new Point(Theme.S(93), Theme.S(52)), Theme.Muted, TextFormatFlags.NoPadding);
        using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, Theme.S(28), Theme.S(88), ClientSize.Width - Theme.S(28), Theme.S(88));
    }

    void OnMain()
    {
        if (failed) { Close(); return; }
        if (finished)
        {
            try { Process.Start(new ProcessStartInfo(System.IO.Path.Combine(SetupMain.Dir, "AngardVPN.exe")) { UseShellExecute = true, WorkingDirectory = SetupMain.Dir }); }
            catch (Exception) { }
            Close();
            return;
        }
        if (busy) return;
        busy = true;
        bool desk = cbDesk.Checked, vpn = cbVpn.Enabled && cbVpn.Checked;
        cbDesk.Enabled = false; cbVpn.Enabled = false;
        btnMain.Text = "Установка…";
        btnClose.Visible = false;
        barBack.Visible = true;
        Thread t = new Thread(delegate()
        {
            string err = null;
            try { SetupMain.Install(desk, vpn, delegate(double f, string s) { Post(delegate { SetProgress(f, s); }); }); }
            catch (Exception ex) { err = ex.Message; SetupMain.Log("ОШИБКА: " + ex); }
            Post(delegate { Finished(err); });
        });
        t.IsBackground = true;
        t.Start();
    }

    void Post(Action a)
    {
        try { BeginInvoke(a); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    void SetProgress(double f, string text)
    {
        barFill.Width = (int)(barBack.Width * Math.Max(0, Math.Min(1, f)));
        status.Text = text;
    }

    void Finished(string err)
    {
        busy = false;
        barBack.Visible = err == null;
        if (err != null)
        {
            status.ForeColor = Theme.Red;
            status.Text = "Не удалось: " + err;
            btnMain.Text = "Закрыть";
            failed = true;
            return;
        }
        finished = true;
        status.ForeColor = Theme.Green;
        status.Text = "Готово. AngardVPN установлен.";
        btnMain.Text = "Запустить AngardVPN";
        btnClose.Text = "Закрыть";
        btnClose.Visible = true;
    }
}
