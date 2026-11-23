using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace MiniVpn
{
    public class VpnServer
    {
        public string HostName;
        public string Ip;
        public long Score;
        public int Ping;
        public long Speed;          // бит/с
        public string CountryLong;
        public string CountryShort;
        public int Sessions;
        public string ConfigBase64;

        /// Задержка до сервера с этого компьютера, мс. -2 — не измерялась (UDP), -1 — сервер не ответил.
        public volatile int Latency = -2;
        string remoteIp;
        int remotePort;
        bool tcp, parsed;

        void ParseEndpoint()
        {
            if (parsed) return;
            if (Vless != null) { remoteIp = Ip; remotePort = Vless.Port; tcp = !Vless.Udp; parsed = Ip != ""; return; }
            try { ConfigBuilder.GetRemote(DecodeConfig(), out remoteIp, out remotePort, out tcp); }
            catch (FormatException) { }
            if (remoteIp == null) remoteIp = Ip;
            parsed = true;
        }

        public string RemoteIp { get { ParseEndpoint(); return remoteIp; } }
        public int RemotePort { get { ParseEndpoint(); return remotePort; } }
        public bool IsTcp { get { ParseEndpoint(); return tcp; } }

        public string LatencyText
        {
            get { return Latency >= 0 ? Latency + " мс" : Latency == -1 ? "нет ответа" : IsTcp ? "" : "UDP"; }
        }

        public string CountryName
        {
            get { return Countries.Name(CountryShort, CountryLong); }
        }

        public string Source = "VPN Gate";
        public VlessLink Vless;          // не null — сервер из подписки VLESS, подключается через sing-box

        // Настоящая проверка VLESS (VlessTester): когда была, отклик через сервер (мс), годен ли.
        public DateTime TestedAt = DateTime.MinValue;
        public int TestMs = -2;
        public bool TestedBad;
        public bool TestedFresh { get { return (DateTime.Now - TestedAt).TotalMinutes < 40; } }

        public string SpeedText
        {
            get
            {
                if (Speed <= 0) return Source;
                string sp = (Speed / 1000000.0).ToString("0", CultureInfo.InvariantCulture) + " Мбит/с";
                // У проверенных серверов sing-box виден протокол: так понятно, что скорость замерена, а не взята из списка.
                return Vless != null && TestedFresh ? Source + " · " + sp : sp;
            }
        }

        public string DecodeConfig()
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(ConfigBase64));
        }
    }

    public static class Countries
    {
        static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"??","Страна не определена"},{"JP","Япония"},{"KR","Южная Корея"},{"RU","Россия"},{"US","США"},{"TH","Таиланд"},
            {"HR","Хорватия"},{"SA","Саудовская Аравия"},{"VN","Вьетнам"},{"FR","Франция"},{"BR","Бразилия"},
            {"UA","Украина"},{"DE","Германия"},{"GB","Великобритания"},{"NL","Нидерланды"},{"CA","Канада"},
            {"CN","Китай"},{"TW","Тайвань"},{"HK","Гонконг"},{"SG","Сингапур"},{"IN","Индия"},
            {"ID","Индонезия"},{"TR","Турция"},{"PL","Польша"},{"ES","Испания"},{"IT","Италия"},
            {"SE","Швеция"},{"FI","Финляндия"},{"NO","Норвегия"},{"RO","Румыния"},{"AU","Австралия"},
            {"MX","Мексика"},{"AR","Аргентина"},{"KZ","Казахстан"},{"GE","Грузия"},{"IL","Израиль"},
            {"AE","ОАЭ"},{"MY","Малайзия"},{"PH","Филиппины"},{"CZ","Чехия"},{"CH","Швейцария"},
            {"AT","Австрия"},{"BE","Бельгия"},{"PT","Португалия"},{"GR","Греция"},{"HU","Венгрия"},
            {"BG","Болгария"},{"RS","Сербия"},{"IR","Иран"},{"EG","Египет"},{"ZA","ЮАР"},
            {"CL","Чили"},{"CO","Колумбия"},{"PE","Перу"},{"NZ","Новая Зеландия"},{"IE","Ирландия"},
            {"DK","Дания"},{"LT","Литва"},{"LV","Латвия"},{"EE","Эстония"},{"MD","Молдова"},
            {"LA","Лаос"},{"MM","Мьянма"},{"KH","Камбоджа"},{"BD","Бангладеш"},{"PK","Пакистан"},{"NP","Непал"},
            {"MN","Монголия"},{"LK","Шри-Ланка"},{"SK","Словакия"},{"SI","Словения"},{"LU","Люксембург"},{"IS","Исландия"},
            {"CY","Кипр"},{"MA","Марокко"},{"TN","Тунис"},{"DZ","Алжир"},{"NG","Нигерия"},{"KE","Кения"},
            {"VE","Венесуэла"},{"EC","Эквадор"},{"UY","Уругвай"},{"TJ","Таджикистан"},{"TM","Туркмения"},{"IQ","Ирак"},
            {"JO","Иордания"},{"KW","Кувейт"},{"QA","Катар"},{"OM","Оман"},{"BA","Босния и Герцеговина"},{"MK","Северная Македония"},
            {"AL","Албания"},{"ME","Черногория"},{"MT","Мальта"},
            {"BY","Беларусь"},{"AM","Армения"},{"AZ","Азербайджан"},{"UZ","Узбекистан"},{"KG","Киргизия"}
        };

        // Примерные центры стран (широта, долгота) — для сортировки по близости, когда замер задержки недостоверен.
        static readonly Dictionary<string, double[]> Geo = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            {"JP",new double[]{36,138}},{"KR",new double[]{36.5,128}},{"RU",new double[]{55.7,37.6}},{"US",new double[]{39,-98}},
            {"TH",new double[]{15,101}},{"HR",new double[]{45,16}},{"SA",new double[]{24,45}},{"VN",new double[]{16,107}},
            {"FR",new double[]{46,2}},{"BR",new double[]{-10,-52}},{"UA",new double[]{49,32}},{"DE",new double[]{51,10}},
            {"GB",new double[]{54,-2}},{"NL",new double[]{52,5}},{"CA",new double[]{56,-106}},{"CN",new double[]{35,105}},
            {"TW",new double[]{23.7,121}},{"HK",new double[]{22.3,114}},{"SG",new double[]{1.3,103.8}},{"IN",new double[]{21,78}},
            {"ID",new double[]{-2,118}},{"TR",new double[]{39,35}},{"PL",new double[]{52,19}},{"ES",new double[]{40,-4}},
            {"IT",new double[]{42.5,12.5}},{"SE",new double[]{62,15}},{"FI",new double[]{64,26}},{"NO",new double[]{61,9}},
            {"RO",new double[]{46,25}},{"AU",new double[]{-25,134}},{"MX",new double[]{23,-102}},{"AR",new double[]{-34,-64}},
            {"KZ",new double[]{48,67}},{"GE",new double[]{42,43.5}},{"IL",new double[]{31.5,35}},{"AE",new double[]{24,54}},
            {"MY",new double[]{3,102}},{"PH",new double[]{13,122}},{"CZ",new double[]{49.8,15.5}},{"CH",new double[]{47,8}},
            {"AT",new double[]{47.5,14}},{"BE",new double[]{50.6,4.6}},{"PT",new double[]{39.5,-8}},{"GR",new double[]{39,22}},
            {"HU",new double[]{47,19.5}},{"BG",new double[]{42.7,25.5}},{"RS",new double[]{44,21}},{"IR",new double[]{32,53}},
            {"EG",new double[]{27,30}},{"ZA",new double[]{-29,24}},{"CL",new double[]{-33,-71}},{"CO",new double[]{4,-73}},
            {"PE",new double[]{-10,-76}},{"NZ",new double[]{-41,174}},{"IE",new double[]{53,-8}},{"DK",new double[]{56,10}},
            {"LT",new double[]{55,24}},{"LV",new double[]{57,25}},{"EE",new double[]{59,26}},{"MD",new double[]{47,28.5}},
            {"BY",new double[]{53.5,28}},{"AM",new double[]{40,45}},{"AZ",new double[]{40.5,47.5}},{"UZ",new double[]{41,64}},
            {"KG",new double[]{41.5,75}}
        };

        /// Страна пользователя по региону Windows, либо null.
        public static string Home()
        {
            try
            {
                string c = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
                return Geo.ContainsKey(c) ? c.ToUpperInvariant() : null;
            }
            catch (Exception) { return null; }
        }

        /// Расстояние между странами в км; -1, если одна из них неизвестна.
        public static int Distance(string a, string b)
        {
            double[] p, q;
            if (a == null || b == null || !Geo.TryGetValue(a, out p) || !Geo.TryGetValue(b, out q)) return -1;
            double r = Math.PI / 180, dLat = (q[0] - p[0]) * r, dLon = (q[1] - p[1]) * r;
            double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(p[0] * r) * Math.Cos(q[0] * r) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return (int)(6371 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(h))));
        }

        public static string Name(string code, string fallback)
        {
            string s;
            if (code != null && Map.TryGetValue(code, out s)) return s;
            return string.IsNullOrEmpty(fallback) ? (code ?? "?") : fallback;
        }
    }

    public static class Paths
    {
        public static string DataDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AngardVPN");
                Directory.CreateDirectory(d);
                return d;
            }
        }
        public static string Cache { get { return Path.Combine(DataDir, "servers.csv"); } }
        public static string Config { get { return Path.Combine(DataDir, "current.ovpn"); } }
        public static string Log { get { return Path.Combine(DataDir, "openvpn.log"); } }
        public static string Seen { get { return Path.Combine(DataDir, "seen.txt"); } }
        public static string AuthFile { get { return Path.Combine(DataDir, "auth.txt"); } }
        public static string FreeCache { get { return Path.Combine(DataDir, "free-vless.txt"); } }
        public static string SubCache { get { return Path.Combine(DataDir, "vless-sub.txt"); } }
        public static string SingConfig { get { return Path.Combine(DataDir, "singbox.json"); } }
        public static string SingLog { get { return Path.Combine(DataDir, "singbox.log"); } }
        public static string BookCache { get { return Path.Combine(DataDir, "vpnbook.txt"); } }
        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.txt"); } }
    }

    public static class Http
    {
        /// Привязка соединения к адресу сетевой карты. Если привязать не удалось, .NET зовёт делегат снова и снова —
        /// без предела это вечный цикл; после трёх попыток соединение идёт обычным путём.
        public static BindIPEndPoint Bind(IPAddress ip)
        {
            return delegate(ServicePoint sp, IPEndPoint remote, int retry) { return retry < 3 ? new IPEndPoint(ip, 0) : null; };
        }

        /// Читает ответ целиком, но не дольше totalMs. Обычный тайм-аут считает только паузу между порциями,
        /// и загрузка по связи в несколько КБ/с тянулась бы бесконечно (так зависала загрузка списка).
        public static string ReadAll(WebResponse resp, int totalMs)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[65536];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (sw.ElapsedMilliseconds > totalMs)
                        throw new WebException("слишком медленная связь: " + ms.Length / 1024 + " КБ за " + sw.ElapsedMilliseconds / 1000 + " с", WebExceptionStatus.Timeout);
                }
                string t = Encoding.UTF8.GetString(ms.ToArray());
                return t.Length > 0 && t[0] == '﻿' ? t.Substring(1) : t;
            }
        }
    }

    public static class ServerList
    {
        public const string Url = "http://www.vpngate.net/api/iphone/";

        /// Разбирает CSV VPN Gate. Битые строки пропускаются.
        public static List<VpnServer> Parse(string csv)
        {
            List<VpnServer> list = new List<VpnServer>();
            if (string.IsNullOrEmpty(csv)) return list;
            string[] lines = csv.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '*' || line[0] == '#') continue;
                string[] c = line.Split(',');
                if (c.Length < 15) continue;
                IPAddress ip;
                if (!IPAddress.TryParse(c[1], out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                string b64 = c[14].Trim();
                if (b64.Length < 100) continue;
                try
                {
                    string cfg = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    if (cfg.IndexOf("remote ", StringComparison.Ordinal) < 0) continue;
                }
                catch (FormatException) { continue; }

                VpnServer s = new VpnServer();
                s.HostName = c[0];
                s.Ip = c[1];
                long.TryParse(c[2], out s.Score);
                int.TryParse(c[3], out s.Ping);
                long.TryParse(c[4], out s.Speed);
                s.CountryLong = c[5];
                s.CountryShort = c[6].ToUpperInvariant();
                int.TryParse(c[7], out s.Sessions);
                s.ConfigBase64 = b64;
                list.Add(s);
            }
            return list;
        }

        static string HostOf(string line)
        {
            string l = line.Trim();
            if (l.Length == 0 || l[0] == '*' || l[0] == '#') return null;
            int i = l.IndexOf(',');
            return i > 0 ? l.Substring(0, i) : null;
        }

        /// Объединяет свежий список с прежним: свежие серверы целиком, из прежних — те, что виделись не раньше maxAge назад.
        /// seen (имя сервера → когда последний раз был в свежем списке) обновляется.
        public static string Merge(string fresh, string cached, IDictionary<string, long> seen, long now, long maxAge)
        {
            StringBuilder sb = new StringBuilder();
            HashSet<string> have = new HashSet<string>();
            foreach (string line in fresh.Split('\n'))
            {
                string h = HostOf(line);
                if (h == null) { if (line.Trim().StartsWith("#")) sb.Append(line.Trim()).Append("\r\n"); continue; }
                if (!have.Add(h)) continue;
                seen[h] = now;
                sb.Append(line.Trim()).Append("\r\n");
            }
            foreach (string line in (cached ?? "").Split('\n'))
            {
                string h = HostOf(line);
                long t;
                if (h == null || have.Contains(h) || !seen.TryGetValue(h, out t) || now - t > maxAge) continue;
                have.Add(h);
                sb.Append(line.Trim()).Append("\r\n");
            }
            List<string> old = new List<string>();
            foreach (KeyValuePair<string, long> p in seen) if (now - p.Value > maxAge) old.Add(p.Key);
            foreach (string k in old) seen.Remove(k);
            return sb.ToString();
        }

        static Dictionary<string, long> ReadSeen()
        {
            Dictionary<string, long> d = new Dictionary<string, long>();
            try
            {
                if (!File.Exists(Paths.Seen)) return d;
                foreach (string line in File.ReadAllLines(Paths.Seen))
                {
                    int i = line.IndexOf('=');
                    long t;
                    if (i > 0 && long.TryParse(line.Substring(i + 1), out t)) d[line.Substring(0, i)] = t;
                }
            }
            catch (IOException) { }
            return d;
        }

        /// Загружает список из сети; при неудаче берёт кэш с диска.
        public static List<VpnServer> Load(out bool fromCache)
        {
            fromCache = false;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url);
                req.Timeout = 15000;
                req.ReadWriteTimeout = 20000;
                req.UserAgent = "AngardVPN";
                // При чужом VPN список качается напрямую через сетевую карту: чужой туннель бывает медленным до нескольких КБ/с.
                IPAddress bind = null;
                foreach (string o in Tunnels.Active()) if (!o.StartsWith("AngardTun", StringComparison.Ordinal)) { bind = Tunnels.PhysicalIp(); break; }
                if (bind != null) req.ServicePoint.BindIPEndPointDelegate = Http.Bind(bind);
                string text;
                using (WebResponse resp = req.GetResponse())
                    text = Http.ReadAll(resp, 45000);
                if (Parse(text).Count > 0)
                {
                    // VPN Gate отдаёт около сотни серверов, и выборка понемногу меняется. Серверы, виденные
                    // за последние три дня, остаются в списке: так набирается больше серверов и стран.
                    Dictionary<string, long> seen = ReadSeen();
                    string cached = "";
                    try { if (File.Exists(Paths.Cache)) cached = File.ReadAllText(Paths.Cache, Encoding.UTF8); } catch (IOException) { }
                    string merged = Merge(text, cached, seen, DateTime.UtcNow.Ticks, TimeSpan.FromDays(3).Ticks);
                    try
                    {
                        File.WriteAllText(Paths.Cache, merged, Encoding.UTF8);
                        List<string> lines = new List<string>();
                        foreach (KeyValuePair<string, long> p in seen) lines.Add(p.Key + "=" + p.Value);
                        File.WriteAllLines(Paths.Seen, lines.ToArray());
                    }
                    catch (IOException) { }
                    return Parse(merged);
                }
            }
            catch (WebException) { }
            catch (IOException) { }

            fromCache = true;
            try
            {
                if (File.Exists(Paths.Cache)) return Parse(File.ReadAllText(Paths.Cache, Encoding.UTF8));
            }
            catch (IOException) { }
            return new List<VpnServer>();
        }
    }

    public static class ConfigBuilder
    {
        static readonly string[] Dropped = { "resolv-retry", "verb", "persist-key", "management", "connect-retry", "data-ciphers-fallback", "ip-win32" };

        /// Способы выдачи адреса адаптеру туннеля, в порядке перебора.
        public static readonly string[] IpMethods = { "netsh", "ipapi", "dynamic" };

        public static string NextIpMethod(string current)
        {
            int i = Array.IndexOf(IpMethods, current);
            return IpMethods[(i + 1) % IpMethods.Length];
        }

        /// Адрес, порт и протокол сервера из конфига.
        public static void GetRemote(string config, out string ip, out int port, out bool tcp)
        {
            ip = null; port = 0; tcp = false;
            foreach (string raw in config.Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("proto ", StringComparison.Ordinal)) tcp = l.IndexOf("tcp", StringComparison.OrdinalIgnoreCase) >= 0;
                else if (l.StartsWith("remote ", StringComparison.Ordinal) && ip == null)
                {
                    string[] p = l.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    IPAddress a;
                    if (p.Length >= 2 && IPAddress.TryParse(p[1], out a)) ip = p[1];
                    if (p.Length >= 3) int.TryParse(p[2], out port);
                }
            }
        }

        /// Возвращает адрес сервера из конфига ("remote ip port"), либо null.
        public static string GetRemoteIp(string config)
        {
            foreach (string raw in config.Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("remote ", StringComparison.Ordinal))
                {
                    string[] p = l.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    IPAddress ip;
                    if (p.Length >= 2 && IPAddress.TryParse(p[1], out ip)) return p[1];
                }
            }
            return null;
        }

        /// Дополняет конфиг VPN Gate настройками приложения.
        public static string Build(string baseConfig, int managementPort, string ipMethod)
        {
            return Build(baseConfig, managementPort, ipMethod, false);
        }

        /// Конфиг для «гонки»: тот же обмен ключами и запрос настроек, но без сетевого адаптера и маршрутов
        /// (dev null). Показывает, принимает ли сервер подключение, ничего не меняя в системе.
        public static string BuildProbe(string baseConfig, int managementPort)
        {
            return Build(baseConfig, managementPort, null, true);
        }

        static string Build(string baseConfig, int managementPort, string ipMethod, bool probe)
        {
            StringBuilder sb = new StringBuilder();
            string cipher = null;
            bool needAuth = false;
            foreach (string raw in baseConfig.Replace("\r", "").Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("cipher ", StringComparison.Ordinal)) cipher = l.Substring(7).Trim();
                bool drop = false;
                foreach (string d in Dropped)
                    if (l == d || l.StartsWith(d + " ", StringComparison.Ordinal)) { drop = true; break; }
                if (probe && l.StartsWith("dev ", StringComparison.Ordinal)) drop = true;
                // Сервер с общим публичным логином: OpenVPN спросил бы его в консоли, поэтому подставляется файл.
                if (l == "auth-user-pass") { drop = true; needAuth = true; }
                if (l == "fast-io" || l.StartsWith("route-delay", StringComparison.Ordinal)) drop = true;
                if (!drop) sb.Append(raw).Append('\n');
            }
            sb.Append("\n# --- AngardVPN ---\n");
            sb.Append("management 127.0.0.1 ").Append(managementPort.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("management-hold\n");
            sb.Append("connect-retry-max 1\n");
            sb.Append("connect-timeout 5\n");
            sb.Append("server-poll-timeout 5\n");
            sb.Append("resolv-retry 0\n");
            if (needAuth) sb.Append("auth-user-pass \"").Append(Paths.AuthFile.Replace('\\', '/')).Append("\"\n");
            if (probe)
            {
                sb.Append("dev null\n");
                sb.Append("ifconfig-noexec\n");
                sb.Append("route-noexec\n");
            }
            else
            {
                if (!string.IsNullOrEmpty(ipMethod)) sb.Append("ip-win32 ").Append(ipMethod).Append('\n');
                sb.Append("redirect-gateway def1\n");
                sb.Append("block-outside-dns\n");
            }
            if (!string.IsNullOrEmpty(cipher)) sb.Append("data-ciphers-fallback ").Append(cipher).Append('\n');
            sb.Append("auth-nocache\n");
            sb.Append("verb 3\n");
            return sb.ToString();
        }
    }

    public static class Mgmt
    {
        /// Разбирает строку ">STATE:время,ИМЯ,описание,локальный_ip,..." management-интерфейса.
        public static bool ParseState(string line, out string name, out string localIp)
        {
            name = null; localIp = "";
            if (line == null || !line.StartsWith(">STATE:", StringComparison.Ordinal)) return false;
            string[] p = line.Substring(7).Split(',');
            if (p.Length < 2) return false;
            name = p[1];
            if (p.Length >= 4) localIp = p[3];
            return true;
        }

        public static bool ParseBytes(string line, out long bytesIn, out long bytesOut)
        {
            bytesIn = 0; bytesOut = 0;
            if (line == null || !line.StartsWith(">BYTECOUNT:", StringComparison.Ordinal)) return false;
            string[] p = line.Substring(11).Split(',');
            return p.Length >= 2 && long.TryParse(p[0], out bytesIn) && long.TryParse(p[1], out bytesOut);
        }
    }

    public static class IpRanges
    {
        public static uint ToUint(string ip)
        {
            byte[] b = IPAddress.Parse(ip).GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public static string ToStr(uint v)
        {
            return (v >> 24) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255);
        }

        /// Все адреса IPv4, кроме перечисленных диапазонов, в формате netsh ("a-b,c-d").
        public static string Complement(List<uint[]> excluded)
        {
            List<uint[]> ex = new List<uint[]>(excluded);
            ex.Sort(delegate(uint[] a, uint[] b) { return a[0].CompareTo(b[0]); });
            List<string> parts = new List<string>();
            ulong next = 0;
            foreach (uint[] r in ex)
            {
                if (r[0] > next) parts.Add(Range((uint)next, r[0] - 1));
                if ((ulong)r[1] + 1 > next) next = (ulong)r[1] + 1;
            }
            if (next <= uint.MaxValue) parts.Add(Range((uint)next, uint.MaxValue));
            return string.Join(",", parts.ToArray());
        }

        static string Range(uint a, uint b)
        {
            return a == b ? ToStr(a) : ToStr(a) + "-" + ToStr(b);
        }

        public static uint[] R(string from, string to)
        {
            return new uint[] { ToUint(from), ToUint(to) };
        }

        /// Адреса интернета: всё, кроме локальных сетей и адреса VPN-сервера.
        public static string InternetExcept(string serverIp)
        {
            return InternetExcept(string.IsNullOrEmpty(serverIp) ? new string[0] : new string[] { serverIp });
        }

        /// То же для нескольких серверов сразу (во время «гонки» разрешены все её участники).
        public static string InternetExcept(IEnumerable<string> serverIps)
        {
            List<uint[]> ex = new List<uint[]>();
            ex.Add(R("0.0.0.0", "0.255.255.255"));
            ex.Add(R("10.0.0.0", "10.255.255.255"));
            ex.Add(R("127.0.0.0", "127.255.255.255"));
            ex.Add(R("169.254.0.0", "169.254.255.255"));
            ex.Add(R("172.16.0.0", "172.31.255.255"));
            ex.Add(R("192.168.0.0", "192.168.255.255"));
            ex.Add(R("224.0.0.0", "255.255.255.255"));
            foreach (string ip in serverIps)
                if (!string.IsNullOrEmpty(ip)) ex.Add(R(ip, ip));
            return Complement(ex);
        }

        /// Все локальные адреса, кроме адреса туннеля.
        public static string AllExcept(string ip)
        {
            List<uint[]> ex = new List<uint[]>();
            ex.Add(R(ip, ip));
            return Complement(ex);
        }
    }

    /// Порядок перебора серверов и замер задержки.
    public static class ServerPicker
    {
        /// selected == null — «Лучший доступный». Сначала последний удачный сервер, затем отвечающие
        /// (ближние раньше, при равной задержке — с лучшим рейтингом), затем непроверенные (UDP),
        /// в конце — уже отказавшие в этом сеансе и не ответившие на замер.
        public static List<VpnServer> Order(List<VpnServer> all, VpnServer selected, ICollection<string> failed, string lastGood)
        {
            return Order(all, selected, failed, lastGood, true, null);
        }

        /// trustLatency == false — замер шёл через чужой VPN и ничего не значит: тогда ближние определяются
        /// по расстоянию от страны home до страны сервера.
        public static List<VpnServer> Order(List<VpnServer> all, VpnServer selected, ICollection<string> failed, string lastGood,
            bool trustLatency, string home)
        {
            List<VpnServer> pool = new List<VpnServer>();
            foreach (VpnServer s in all)
                if (selected == null || s == selected || s.CountryShort == selected.CountryShort) pool.Add(s);
            pool.Sort(delegate(VpnServer a, VpnServer b)
            {
                int c = Rank(a, selected, failed, lastGood).CompareTo(Rank(b, selected, failed, lastGood));
                if (c != 0) return c;
                if (!trustLatency)
                {
                    c = Near(home, a).CompareTo(Near(home, b));
                    if (c != 0) return c;
                }
                else if (a.Latency >= 0 && b.Latency >= 0)
                {
                    c = (a.Latency / 50).CompareTo(b.Latency / 50);
                    if (c != 0) return c;
                }
                c = b.Speed.CompareTo(a.Speed);
                return c != 0 ? c : b.Score.CompareTo(a.Score);
            });
            return pool;
        }

        /// Выбирает до total серверов, не более perCountry из одной страны (по порядку списка): иначе быстрые адреса
        /// одной страны вытесняют остальные. Если места остаётся больше, добирает пропущенными.
        public static List<VpnServer> Diverse(List<VpnServer> ordered, int total, int perCountry)
        {
            List<VpnServer> res = new List<VpnServer>(), rest = new List<VpnServer>();
            Dictionary<string, int> n = new Dictionary<string, int>();
            foreach (VpnServer s in ordered)
            {
                int c;
                n.TryGetValue(s.CountryShort, out c);
                if (res.Count < total && c < perCountry) { res.Add(s); n[s.CountryShort] = c + 1; }
                else rest.Add(s);
            }
            for (int i = 0; i < rest.Count && res.Count < total; i++) res.Add(rest[i]);
            return res;
        }
        static int Near(string home, VpnServer s)
        {
            int d = Countries.Distance(home, s.CountryShort);
            return d < 0 ? 99 : d / 700;
        }

        static int Rank(VpnServer s, VpnServer selected, ICollection<string> failed, string lastGood)
        {
            if (s == selected) return 0;
            if (s.Vless != null && s.TestedBad && s.TestedFresh) return 6;     // настоящая проверка не прошла
            bool bad = failed != null && failed.Contains(s.HostName);
            if (s.Latency == -1) return 6;
            if (bad) return 5;
            return s.Latency >= 0 ? 2 : 3;
        }

        /// Одновременно пробует соединиться со всеми TCP-серверами и записывает задержку в Latency.
        public static void Measure(List<VpnServer> servers, int timeoutMs)
        {
            Measure(servers, timeoutMs, false);
        }

        /// availabilityOnly — замер идёт через работающий туннель: задержка получится завышенной,
        /// поэтому обновляется только признак «отвечает / не отвечает», а прежние значения сохраняются.
        public static void Measure(List<VpnServer> servers, int timeoutMs, bool availabilityOnly)
        {
            if (availabilityOnly)
            {
                List<VpnServer> copies = new List<VpnServer>();
                foreach (VpnServer s in servers)
                {
                    VpnServer c = new VpnServer();
                    c.ConfigBase64 = s.ConfigBase64; c.Ip = s.Ip; c.Vless = s.Vless;
                    copies.Add(c);
                }
                Measure(copies, timeoutMs, false);
                for (int i = 0; i < servers.Count; i++)
                {
                    int now = copies[i].Latency, was = servers[i].Latency;
                    if (now == -1) servers[i].Latency = -1;
                    else if (now >= 0 && was < 0) servers[i].Latency = now;
                }
                return;
            }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int pending = 0;
            List<System.Net.Sockets.TcpClient> clients = new List<System.Net.Sockets.TcpClient>();
            // Чужой VPN завернул бы замер в свой туннель, и задержка вышла бы ложной: тогда меряем напрямую с сетевой карты.
            IPAddress bind = null;
            foreach (string o in Tunnels.Active()) if (!o.StartsWith("AngardTun", StringComparison.Ordinal)) { bind = Tunnels.PhysicalIp(); break; }
            foreach (VpnServer srv in servers)
            {
                if (!srv.IsTcp || srv.RemotePort <= 0 || string.IsNullOrEmpty(srv.RemoteIp)) { srv.Latency = -2; continue; }
                VpnServer s = srv;
                System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient();
                if (bind != null) try { c.Client.Bind(new IPEndPoint(bind, 0)); } catch (Exception) { }
                clients.Add(c);
                System.Threading.Interlocked.Increment(ref pending);
                s.Latency = -1;
                try
                {
                    c.BeginConnect(IPAddress.Parse(s.RemoteIp), s.RemotePort, delegate(IAsyncResult ar)
                    {
                        try
                        {
                            c.EndConnect(ar);
                            if (sw.ElapsedMilliseconds <= timeoutMs) s.Latency = (int)sw.ElapsedMilliseconds;
                        }
                        catch (Exception) { }
                        System.Threading.Interlocked.Decrement(ref pending);
                    }, null);
                }
                catch (Exception) { System.Threading.Interlocked.Decrement(ref pending); }
            }
            while (System.Threading.Interlocked.CompareExchange(ref pending, 0, 0) > 0 && sw.ElapsedMilliseconds < timeoutMs)
                System.Threading.Thread.Sleep(25);
            foreach (System.Net.Sockets.TcpClient c in clients)
                try { c.Close(); } catch (Exception) { }
        }
    }

    /// Проверка, что интернет действительно идёт (после подключения — через туннель).
    public static class NetCheck
    {
        static readonly string[] Urls = {
            "http://www.msftconnecttest.com/connecttest.txt",
            "http://www.gstatic.com/generate_204",
            "http://1.1.1.1/"          // без DNS: отличает «не работает DNS» от «не идёт трафик»
        };

        /// Сразу после поднятия туннеля адаптеру нужно несколько секунд, поэтому проверка повторяется,
        /// пока не пройдёт или не истечёт budgetMs. Любой HTTP-ответ считается успехом.
        /// detail — что сработало либо последняя ошибка.
        /// Из того, что ввёл пользователь ("https://Author.Today/work/1"), оставляет имя сайта ("author.today").
        public static string CleanHost(string input)
        {
            string s = (input ?? "").Trim().ToLowerInvariant();
            int i = s.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) s = s.Substring(i + 3);
            i = s.IndexOfAny(new char[] { '/', '?', '#', ' ' });
            if (i >= 0) s = s.Substring(0, i);
            i = s.LastIndexOf('@');
            if (i >= 0) s = s.Substring(i + 1);
            i = s.IndexOf(':');
            if (i >= 0) s = s.Substring(0, i);
            foreach (char c in s)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-')) return "";
            return s.Trim('.');
        }

        [System.Runtime.InteropServices.DllImport("dnsapi.dll")]
        static extern bool DnsFlushResolverCache();

        public static bool Works(int budgetMs, Func<bool> keepGoing, out string detail)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            detail = "";
            int round = 0;
            while (keepGoing())
            {
                round++;
                // Windows запоминает неудачные DNS-запросы; без сброса проверка видела бы старую ошибку.
                try { DnsFlushResolverCache(); } catch (Exception) { }
                foreach (string url in Urls)
                {
                    try
                    {
                        HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                        r.Timeout = 4000;
                        r.Proxy = null;
                        r.KeepAlive = false;
                        r.AllowAutoRedirect = false;
                        using (r.GetResponse()) { }
                        detail = Ok(url, round, sw);
                        return true;
                    }
                    catch (WebException e)
                    {
                        if (e.Response != null) { detail = Ok(url, round, sw); return true; }
                        detail = new Uri(url).Host + ": " + e.Status;
                    }
                    catch (Exception e) { detail = new Uri(url).Host + ": " + e.GetType().Name; }
                    if (!keepGoing()) return false;
                }
                if (sw.ElapsedMilliseconds >= budgetMs) break;
                System.Threading.Thread.Sleep(600);
            }
            detail += ", попыток " + round + " за " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " с";
            return false;
        }

        static string Ok(string url, int round, System.Diagnostics.Stopwatch sw)
        {
            return new Uri(url).Host + " ответил с попытки " + round + " через " +
                (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " с";
        }
    }

    /// Запуск вместе с Windows. Приложению нужны права администратора, поэтому обычная «Автозагрузка» не подходит:
    /// используется задание планировщика с наивысшими правами.
    public static class AutoStart
    {
        const string Task = "AngardVPN";

        static int Schtasks(string args)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo si = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(si))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception) { return -1; }
        }

        public static bool Enabled { get { return Schtasks("/query /tn \"" + Task + "\"") == 0; } }

        public static bool Set(bool on, string exePath)
        {
            if (!on) return Schtasks("/delete /tn \"" + Task + "\" /f") == 0;
            return Schtasks("/create /tn \"" + Task + "\" /tr \"\\\"" + exePath + "\\\" --tray\" /sc onlogon /rl highest /f") == 0;
        }
    }

    /// Чужие VPN-туннели: два VPN одновременно обычно мешают друг другу.
    public static class Tunnels
    {
        static readonly string[] Marks = { "sing-tun", "wintun", "wireguard", "windscribe", "planet", "tunnel", "vpn", "nord", "proton", "outline" };
        static readonly string[] Harmless = { "tap-windows adapter v9", "radmin", "hamachi", "zerotier", "tailscale" };

        public static bool IsForeign(string description)
        {
            if (string.IsNullOrEmpty(description)) return false;
            string d = description.ToLowerInvariant();
            foreach (string h in Harmless) if (d.Contains(h)) return false;
            foreach (string m in Marks) if (d.Contains(m)) return true;
            return false;
        }

        /// Сетевая карта, через которую идёт обычный интернет (Ethernet / Wi-Fi со шлюзом), — чтобы обойти чужой VPN. null — не найдена.
        public static string PhysicalInterface()
        {
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Ethernet &&
                        n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 &&
                        n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet) continue;
                    string d = (n.Description ?? "").ToLowerInvariant();
                    if (IsForeign(n.Description) || d.Contains("tap-windows") || d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vmware") ||
                        d.Contains("radmin") || d.Contains("hamachi") || d.Contains("zerotier") || d.Contains("tailscale") || n.Name.StartsWith("AngardTun", StringComparison.Ordinal)) continue;
                    foreach (System.Net.NetworkInformation.GatewayIPAddressInformation g in n.GetIPProperties().GatewayAddresses)
                        if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any))
                            return n.Name;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// IPv4-адрес сетевой карты из PhysicalInterface; null — нет.
        public static System.Net.IPAddress PhysicalIp()
        {
            string name = PhysicalInterface();
            if (name == null) return null;
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    if (n.Name == name)
                        foreach (System.Net.NetworkInformation.UnicastIPAddressInformation a in n.GetIPProperties().UnicastAddresses)
                            if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return a.Address;
            }
            catch (Exception) { }
            return null;
        }

        /// Названия активных чужих туннелей.
        public static List<string> Active()
        {
            List<string> res = new List<string>();
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    if (n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && IsForeign(n.Description))
                        res.Add(n.Name);
            }
            catch (Exception) { }
            return res;
        }
    }

    public class Settings
    {
        public bool KillSwitch = true;
        public string Selected = "";     // имя сервера или "" для «Лучший доступный»
        public bool TrayHintShown;
        public string IpMethod = "netsh";
        public string LastGood = "";
        public bool AutoConnect;         // подключаться сразу при запуске
        public bool StartHidden;         // запускаться свёрнутым в трей
        public bool Notify = true;       // всплывающие уведомления
        public string MonitorHost = "author.today";   // сайт для живого монитора отклика
        public bool FreeServers = true;                 // общедоступные серверы VLESS из открытого репозитория
        public string Subscription = "";              // адрес подписки VLESS или сами ссылки
        public bool OnTop;               // окно поверх всех окон
        public bool Compact;             // окно свёрнуто в одну строку

        public static Settings Load()
        {
            Settings s = new Settings();
            string selVer = "";
            try
            {
                if (!File.Exists(Paths.SettingsFile)) return s;
                foreach (string line in File.ReadAllLines(Paths.SettingsFile))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    if (k == "killswitch") s.KillSwitch = v == "1";
                    else if (k == "selected") s.Selected = v;
                    else if (k == "trayhint") s.TrayHintShown = v == "1";
                    else if (k == "ipmethod" && Array.IndexOf(ConfigBuilder.IpMethods, v) >= 0) s.IpMethod = v;
                    else if (k == "lastgood") s.LastGood = v;
                    else if (k == "autoconnect") s.AutoConnect = v == "1";
                    else if (k == "starthidden") s.StartHidden = v == "1";
                    else if (k == "notify") s.Notify = v == "1";
                    else if (k == "monitor") s.MonitorHost = NetCheck.CleanHost(v);
                    else if (k == "subscription") s.Subscription = v;
                    else if (k == "freeservers") s.FreeServers = v == "1";
                    else if (k == "selver") selVer = v;
                    else if (k == "ontop") s.OnTop = v == "1";
                    else if (k == "compact") s.Compact = v == "1";
                }
                // Версия 2.4: выбор по задержке и скорости без привязки к стране. Сохранённая прежними версиями страна
                // (часто Япония, медленная отсюда) один раз сбрасывается на «Лучший доступный».
                if (selVer != "24") s.Selected = "";
            }
            catch (IOException) { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(Paths.SettingsFile, new string[] {
                    "killswitch=" + (KillSwitch ? "1" : "0"),
                    "selected=" + Selected,
                    "selver=24",
                    "ontop=" + (OnTop ? "1" : "0"),
                    "compact=" + (Compact ? "1" : "0"),
                    "trayhint=" + (TrayHintShown ? "1" : "0"),
                    "ipmethod=" + IpMethod,
                    "lastgood=" + LastGood,
                    "autoconnect=" + (AutoConnect ? "1" : "0"),
                    "starthidden=" + (StartHidden ? "1" : "0"),
                    "notify=" + (Notify ? "1" : "0"),
                    "monitor=" + MonitorHost,
                    "freeservers=" + (FreeServers ? "1" : "0"),
                    "subscription=" + Subscription.Replace("\r", "").Replace("\n", " ")
                });
            }
            catch (IOException) { }
        }
    }

    public static class Fmt
    {
        public static string Bytes(long b)
        {
            if (b < 1024) return b + " Б";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " КБ";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " МБ";
            return (b / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " ГБ";
        }
    }
}
