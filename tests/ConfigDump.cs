using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MiniVpn;

// Пишет рабочий конфиг приложения (режим адаптера) для серверов из файла строк "CC|ссылка": по 40 каждого протокола.
// Его затем проверяет «sing-box check» — так проверяется, что ядро принимает все виды исходящих. Аргументы: <строки> <выход> [сетевая карта]
static class ConfigDump
{
    static int Main(string[] a)
    {
        List<VpnServer> all = Vless.FreeServers(new List<string>(File.ReadAllLines(a[0], Encoding.UTF8)));
        Dictionary<string, int> n = new Dictionary<string, int>();
        List<VpnServer> use = new List<VpnServer>();
        foreach (VpnServer s in all)
        {
            int c; n.TryGetValue(s.Vless.Proto, out c);
            if (c >= 40) continue;
            n[s.Vless.Proto] = c + 1;
            s.Ip = "1.2.3." + (use.Count % 250 + 1);
            use.Add(s);
        }
        File.WriteAllText(a[1], Vless.Config(use, 9090, true, 0, a.Length > 2 ? a[2] : null), new UTF8Encoding(false));
        foreach (KeyValuePair<string, int> p in n) Console.WriteLine(p.Key + ": " + p.Value);
        return 0;
    }
}
