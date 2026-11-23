using System;
using System.Diagnostics;

namespace MiniVpn
{
    /// Защита от утечек через брандмауэр Windows.
    /// Блокирующее правило сильнее разрешающих, поэтому одного правила достаточно:
    /// запрещаем исходящие в интернет со всех локальных адресов, кроме адреса туннеля.
    public static class KillSwitch
    {
        const string NameA = "AngardVPN KillSwitch A";
        const string NameB = "AngardVPN KillSwitch B";
        const string NameV6 = "AngardVPN IPv6";
        static bool useB;
        static readonly object sync = new object();

        static int Netsh(string args)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo("netsh.exe", args);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                using (Process p = Process.Start(si))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception) { return -1; }
        }

        static bool Exists(string name)
        {
            return Netsh("advfirewall firewall show rule name=\"" + name + "\"") == 0;
        }

        static void Delete(string name)
        {
            Netsh("advfirewall firewall delete rule name=\"" + name + "\"");
        }

        /// Включён ли брандмауэр Windows в текущем сетевом профиле. Без него защита не работает.
        public static bool FirewallEnabled()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                dynamic fw = Activator.CreateInstance(t);
                int profiles = fw.CurrentProfileTypes;
                for (int p = 1; p <= 4; p <<= 1)
                    if ((profiles & p) != 0 && !(bool)fw.FirewallEnabled[p]) return false;
                return true;
            }
            catch (Exception) { return true; }
        }

        /// Блокирует интернет мимо туннеля. tunnelIp == null — туннеля ещё нет: разрешён только сам VPN-сервер.
        /// Новое правило добавляется до удаления старого, чтобы не было окна без защиты.
        public static bool Apply(string serverIp, string tunnelIp)
        {
            return Apply(new string[] { serverIp }, tunnelIp);
        }

        /// То же, но разрешено несколько серверов сразу — на время «гонки».
        public static bool Apply(System.Collections.Generic.IEnumerable<string> serverIp, string tunnelIp)
        {
            lock (sync)
            {
                string add = useB ? NameB : NameA, old = useB ? NameA : NameB;
                string args = "advfirewall firewall add rule name=\"" + add + "\" dir=out action=block enable=yes profile=any" +
                              " remoteip=" + IpRanges.InternetExcept(serverIp);
                if (!string.IsNullOrEmpty(tunnelIp)) args += " localip=" + IpRanges.AllExcept(tunnelIp);
                Delete(add);
                bool ok = Netsh(args) == 0;
                if (ok)
                {
                    Delete(old);
                    useB = !useB;
                }
                return ok;
            }
        }

        /// VPN Gate работает только по IPv4, поэтому IPv6 на время сеанса закрывается целиком.
        public static void BlockV6()
        {
            lock (sync)
            {
                if (Exists(NameV6)) return;
                Netsh("advfirewall firewall add rule name=\"" + NameV6 + "\" dir=out action=block enable=yes profile=any remoteip=2000::/3");
            }
        }

        public static bool IsActive()
        {
            return Exists(NameA) || Exists(NameB) || Exists(NameV6);
        }

        /// Действует ли сейчас блокировка IPv4 (интернет мимо туннеля закрыт).
        public static bool IsBlocking()
        {
            return Exists(NameA) || Exists(NameB);
        }

        public static void RemoveV4()
        {
            lock (sync) { Delete(NameA); Delete(NameB); }
        }

        public static void Remove()
        {
            lock (sync) { Delete(NameA); Delete(NameB); Delete(NameV6); }
        }
    }
}
