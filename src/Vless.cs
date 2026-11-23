using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace MiniVpn
{
    /// Один сервер из подписки VLESS.
    public class VlessLink
    {
        public string Name = "", Host, Uuid, Flow = "", Security = "none", Sni = "", Fp = "", Pbk = "", Sid = "";
        public string Type = "tcp", Path = "", HostHeader = "", ServiceName = "", Alpn = "";
        public int Port;
        public bool Insecure;
        // Протокол: vless, trojan, vmess, shadowsocks, hysteria2. Для trojan/ss/hysteria2 пароль хранится в Uuid.
        public string Proto = "vless";
        public string Method = "";          // шифр Shadowsocks либо шифрование VMess
        public int AlterId;
        public string Obfs = "", ObfsPassword = "";   // Hysteria2
        public bool Udp { get { return Proto == "hysteria2"; } }

        /// Подпись протокола для списка и строки состояния.
        public string Label
        {
            get
            {
                switch (Proto)
                {
                    case "trojan": return "Trojan";
                    case "vmess": return "VMess";
                    case "shadowsocks": return "Shadowsocks";
                    case "hysteria2": return "Hysteria2";
                    default: return "VLESS";
                }
            }
        }
    }

    /// Подписка VLESS: разбор ссылок и сборка конфигурации sing-box.
    public static class Vless
    {
        static Dictionary<string, string> Query(string q)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in q.Split('&'))
            {
                int i = part.IndexOf('=');
                if (i <= 0) continue;
                try { d[part.Substring(0, i)] = Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' ')); }
                catch (UriFormatException) { }
            }
            return d;
        }

        static string Get(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : "";
        }

        static readonly string[] Schemes = { "vless://", "trojan://", "vmess://", "ss://", "hysteria2://", "hy2://" };

        /// Строка похожа на ссылку поддерживаемого протокола.
        public static bool IsLink(string line)
        {
            string t = (line ?? "").TrimStart();
            foreach (string sc in Schemes) if (t.StartsWith(sc, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static readonly string[] SsMethods = { "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
            "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305" };

        static string B64(string t)
        {
            string b = (t ?? "").Trim().Replace("\r", "").Replace("\n", "").Replace('-', '+').Replace('_', '/');
            int pad = b.IndexOf('=');
            if (pad >= 0) b = b.Substring(0, pad);
            while (b.Length % 4 != 0) b += "=";
            return Encoding.UTF8.GetString(Convert.FromBase64String(b));
        }

        /// Разбирает ссылку vless:// trojan:// vmess:// ss:// hysteria2://; null — повреждена или не поддерживается в sing-box.
        public static VlessLink Parse(string uri)
        {
            string t = (uri ?? "").Trim();
            try
            {
                if (t.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseUrl(t.Substring(8), "vless");
                if (t.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) return ParseUrl(t.Substring(9), "trojan");
                if (t.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)) return ParseUrl(t.Substring(12), "hysteria2");
                if (t.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) return ParseUrl(t.Substring(6), "hysteria2");
                if (t.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(t.Substring(8));
                if (t.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseSs(t.Substring(5));
            }
            catch (FormatException) { }
            catch (ArgumentException) { }
            return null;
        }

        static bool SplitHostPort(string hp, VlessLink l)
        {
            hp = hp.TrimEnd('/');
            int colon = hp.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(hp.Substring(colon + 1), out l.Port) || l.Port <= 0 || l.Port > 65535) return false;
            l.Host = hp.Substring(0, colon).Trim('[', ']');
            return l.Host.Length > 0;
        }

        /// vmess://base64(JSON) в формате v2rayN.
        static VlessLink ParseVmess(string body)
        {
            string json = B64(body);
            VlessLink l = new VlessLink();
            l.Proto = "vmess";
            l.Name = JStr(json, "ps");
            l.Host = JStr(json, "add");
            int.TryParse(JStr(json, "port"), out l.Port);
            l.Uuid = JStr(json, "id");
            int.TryParse(JStr(json, "aid"), out l.AlterId);
            l.Method = JStr(json, "scy");
            if (l.Method == "") l.Method = "auto";
            l.Type = JStr(json, "net").ToLowerInvariant();
            if (l.Type == "" || l.Type == "raw") l.Type = "tcp";
            if (l.Type == "tcp" && JStr(json, "type") != "" && JStr(json, "type") != "none") return null;   // HTTP-маскировка tcp не поддерживается
            l.HostHeader = JStr(json, "host");
            l.Path = JStr(json, "path");
            if (l.Type == "grpc") l.ServiceName = l.Path;
            l.Security = JStr(json, "tls").ToLowerInvariant() == "tls" ? "tls" : "none";
            l.Sni = JStr(json, "sni");
            l.Alpn = JStr(json, "alpn");
            l.Fp = JStr(json, "fp");
            if (l.Host == "" || l.Uuid == "" || l.Port <= 0 || l.Port > 65535) return null;
            if (l.Type != "tcp" && l.Type != "ws" && l.Type != "grpc" && l.Type != "httpupgrade") return null;
            return l;
        }

        /// Значение поля JSON (строка или число) без разбора всего документа: vmess-ссылки устроены просто.
        static string JStr(string json, string key)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\"((?:[^\"\\\\]|\\\\.)*)\"|(-?\\d+))");
            if (!m.Success) return "";
            if (m.Groups[3].Success) return m.Groups[3].Value;
            return System.Text.RegularExpressions.Regex.Unescape(m.Groups[2].Value).Trim();
        }

        /// ss://base64(method:password)@host:port#name (SIP002) либо ss://base64(method:password@host:port)#name.
        static VlessLink ParseSs(string body)
        {
            VlessLink l = new VlessLink();
            l.Proto = "shadowsocks";
            int i = body.IndexOf('#');
            if (i >= 0)
            {
                try { l.Name = Uri.UnescapeDataString(body.Substring(i + 1)).Trim(); } catch (UriFormatException) { l.Name = body.Substring(i + 1); }
                body = body.Substring(0, i);
            }
            i = body.IndexOf('?');
            if (i >= 0)
            {
                if (Get(Query(body.Substring(i + 1)), "plugin") != "") return null;   // плагины (obfs, v2ray-plugin) не поддерживаются
                body = body.Substring(0, i);
            }
            string user, hp;
            int at = body.LastIndexOf('@');
            if (at > 0)
            {
                user = body.Substring(0, at);
                hp = body.Substring(at + 1);
                string dec = user;
                try { dec = Uri.UnescapeDataString(user); } catch (UriFormatException) { }
                if (dec.IndexOf(':') < 0) user = B64(user); else user = dec;
            }
            else
            {
                string all = B64(body);
                at = all.LastIndexOf('@');
                if (at <= 0) return null;
                user = all.Substring(0, at);
                hp = all.Substring(at + 1);
            }
            int c = user.IndexOf(':');
            if (c <= 0) return null;
            l.Method = user.Substring(0, c).ToLowerInvariant();
            l.Uuid = user.Substring(c + 1);
            if (Array.IndexOf(SsMethods, l.Method) < 0 || l.Uuid == "") return null;
            if (!SplitHostPort(hp, l)) return null;
            return l;
        }

        /// Общий вид scheme://пароль@host:port?параметры#имя (vless, trojan, hysteria2).
        static VlessLink ParseUrl(string s, string proto)
        {
            VlessLink l = new VlessLink();
            l.Proto = proto;
            int i = s.IndexOf('#');
            if (i >= 0)
            {
                try { l.Name = Uri.UnescapeDataString(s.Substring(i + 1)).Trim(); } catch (UriFormatException) { l.Name = s.Substring(i + 1); }
                s = s.Substring(0, i);
            }
            string query = "";
            i = s.IndexOf('?');
            if (i >= 0) { query = s.Substring(i + 1); s = s.Substring(0, i); }
            i = s.LastIndexOf('@');
            if (i <= 0) return null;
            l.Uuid = s.Substring(0, i);
            if (proto != "vless") { try { l.Uuid = Uri.UnescapeDataString(l.Uuid); } catch (UriFormatException) { } }
            if (!SplitHostPort(s.Substring(i + 1), l)) return null;
            if (l.Uuid.Length == 0) return null;

            Dictionary<string, string> q = Query(query);
            if (proto == "hysteria2")
            {
                l.Security = "tls";
                l.Sni = Get(q, "sni");
                l.Insecure = Get(q, "insecure") == "1" || Get(q, "allowInsecure") == "1";
                l.Obfs = Get(q, "obfs");
                l.ObfsPassword = Get(q, "obfs-password");
                if (l.Obfs != "" && l.Obfs != "salamander") return null;
                return l;
            }
            l.Type = Get(q, "type").ToLowerInvariant();
            if (l.Type == "" || l.Type == "raw") l.Type = "tcp";
            if (l.Type != "tcp" && l.Type != "ws" && l.Type != "grpc" && l.Type != "httpupgrade") return null;   // xhttp и прочее sing-box не умеет
            l.Security = Get(q, "security").ToLowerInvariant();
            if (l.Security == "") l.Security = proto == "trojan" ? "tls" : "none";
            if (l.Security != "none" && l.Security != "tls" && l.Security != "reality") return null;
            // Ядро знает только xtls-rprx-vision; «-udp443» — его вариант из Xray, остальные устаревшие режимы не работают.
            l.Flow = Get(q, "flow").ToLowerInvariant();
            if (l.Flow == "none") l.Flow = "";
            if (l.Flow.StartsWith("xtls-rprx-vision", StringComparison.Ordinal)) l.Flow = "xtls-rprx-vision";
            else if (l.Flow != "") return null;
            // Отпечаток TLS: незнакомое ядру имя валит запуск, такое заменяется обычным chrome.
            l.Fp = Get(q, "fp").ToLowerInvariant();
            if (l.Fp != "" && Array.IndexOf(new string[] { "chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized" }, l.Fp) < 0) l.Fp = "chrome";
            l.Sni = Get(q, "sni");
            if (l.Sni == "") l.Sni = Get(q, "peer");
            l.Pbk = Get(q, "pbk");
            l.Sid = Get(q, "sid");
            l.Path = Get(q, "path");
            l.HostHeader = Get(q, "host");
            l.ServiceName = Get(q, "serviceName");
            l.Alpn = Get(q, "alpn");
            l.Insecure = Get(q, "allowInsecure") == "1" || Get(q, "insecure") == "1";
            if (l.Security == "reality" && l.Pbk == "") return null;
            return l;
        }

        /// Тело подписки → строки со ссылками. Подписка обычно закодирована в base64 целиком.
        public static List<string> SplitSubscription(string body)
        {
            string t = (body ?? "").Trim();
            if (t.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                try
                {
                    string b = t.Replace("\r", "").Replace("\n", "").Replace('-', '+').Replace('_', '/');
                    while (b.Length % 4 != 0) b += "=";
                    t = Encoding.UTF8.GetString(Convert.FromBase64String(b));
                }
                catch (FormatException) { }
            }
            List<string> res = new List<string>();
            foreach (string line in t.Split('\n'))
                if (line.Trim().Length > 0) res.Add(line.Trim());
            return res;
        }

        /// Код страны из флага в названии сервера («🇳🇱 Нидерланды» → NL); null, если флага нет.
        public static string CountryFromName(string name)
        {
            string n = name ?? "";
            for (int i = 0; i + 3 < n.Length; i++)
            {
                if (!char.IsHighSurrogate(n[i]) || !char.IsHighSurrogate(n[i + 2])) continue;
                int a = char.ConvertToUtf32(n, i) - 0x1F1E6, b = char.ConvertToUtf32(n, i + 2) - 0x1F1E6;
                if (a >= 0 && a < 26 && b >= 0 && b < 26) return "" + (char)('A' + a) + (char)('A' + b);
            }
            return null;
        }

        /// Название без флага и лишних пробелов.
        public static string CleanName(string name)
        {
            StringBuilder sb = new StringBuilder();
            string n = name ?? "";
            for (int i = 0; i < n.Length; i++)
            {
                if (char.IsHighSurrogate(n[i]) && i + 1 < n.Length)
                {
                    int cp = char.ConvertToUtf32(n, i);
                    i++;
                    if (cp >= 0x1F1E6 && cp <= 0x1F1FF) continue;
                    sb.Append(char.ConvertFromUtf32(cp));
                }
                else sb.Append(n[i]);
            }
            return sb.ToString().Trim(' ', '-', '|', '·');
        }

        public static string J(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        /// Исходящее подключение sing-box для одного сервера. server — адрес, по которому соединяться
        /// (IP, если имя уже разрешено: тогда правило блокировки и sing-box смотрят на один и тот же адрес).
        public static string Outbound(VlessLink l, string tag, string server)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"type\":").Append(J(l.Proto)).Append(",\"tag\":").Append(J(tag));
            sb.Append(",\"server\":").Append(J(string.IsNullOrEmpty(server) ? l.Host : server));
            sb.Append(",\"server_port\":").Append(l.Port.ToString(CultureInfo.InvariantCulture));
            switch (l.Proto)
            {
                case "shadowsocks":
                    sb.Append(",\"method\":").Append(J(l.Method)).Append(",\"password\":").Append(J(l.Uuid));
                    return sb.Append("}").ToString();
                case "hysteria2":
                    sb.Append(",\"password\":").Append(J(l.Uuid));
                    if (l.Obfs != "") sb.Append(",\"obfs\":{\"type\":\"salamander\",\"password\":").Append(J(l.ObfsPassword)).Append("}");
                    sb.Append(",\"tls\":{\"enabled\":true,\"server_name\":").Append(J(l.Sni != "" ? l.Sni : l.Host));
                    if (l.Insecure) sb.Append(",\"insecure\":true");
                    sb.Append(",\"alpn\":[\"h3\"]}");
                    return sb.Append("}").ToString();
                case "trojan":
                    sb.Append(",\"password\":").Append(J(l.Uuid));
                    break;
                case "vmess":
                    sb.Append(",\"uuid\":").Append(J(l.Uuid)).Append(",\"security\":").Append(J(l.Method == "" ? "auto" : l.Method));
                    if (l.AlterId > 0) sb.Append(",\"alter_id\":").Append(l.AlterId.ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"packet_encoding\":\"xudp\"");
                    break;
                default:
                    sb.Append(",\"uuid\":").Append(J(l.Uuid));
                    if (l.Flow != "" && l.Type == "tcp") sb.Append(",\"flow\":").Append(J(l.Flow));
                    sb.Append(",\"packet_encoding\":\"xudp\"");
                    break;
            }
            if (l.Security != "none")
            {
                sb.Append(",\"tls\":{\"enabled\":true,\"server_name\":").Append(J(l.Sni != "" ? l.Sni : (l.HostHeader != "" ? l.HostHeader : l.Host)));
                if (l.Insecure) sb.Append(",\"insecure\":true");
                if (l.Alpn != "")
                {
                    List<string> a = new List<string>();
                    foreach (string p in l.Alpn.Split(',')) if (p.Trim() != "") a.Add(J(p.Trim()));
                    if (a.Count > 0) sb.Append(",\"alpn\":[").Append(string.Join(",", a.ToArray())).Append("]");
                }
                if (l.Fp != "" || l.Security == "reality")
                    sb.Append(",\"utls\":{\"enabled\":true,\"fingerprint\":").Append(J(l.Fp != "" ? l.Fp : "chrome")).Append("}");
                if (l.Security == "reality")
                    sb.Append(",\"reality\":{\"enabled\":true,\"public_key\":").Append(J(l.Pbk)).Append(",\"short_id\":").Append(J(l.Sid)).Append("}");
                sb.Append("}");
            }
            if (l.Type == "ws")
            {
                string path = l.Path == "" ? "/" : l.Path;
                int early = 0, ed = path.IndexOf("?ed=", StringComparison.Ordinal);
                if (ed >= 0) { int.TryParse(path.Substring(ed + 4), out early); path = path.Substring(0, ed); }
                sb.Append(",\"transport\":{\"type\":\"ws\",\"path\":").Append(J(path));
                if (l.HostHeader != "") sb.Append(",\"headers\":{\"Host\":").Append(J(l.HostHeader)).Append("}");
                if (early > 0) sb.Append(",\"max_early_data\":").Append(early.ToString(CultureInfo.InvariantCulture)).Append(",\"early_data_header_name\":\"Sec-WebSocket-Protocol\"");
                sb.Append("}");
            }
            else if (l.Type == "grpc")
                sb.Append(",\"transport\":{\"type\":\"grpc\",\"service_name\":").Append(J(l.ServiceName)).Append("}");
            else if (l.Type == "httpupgrade")
            {
                sb.Append(",\"transport\":{\"type\":\"httpupgrade\",\"path\":").Append(J(l.Path == "" ? "/" : l.Path));
                if (l.HostHeader != "") sb.Append(",\"host\":").Append(J(l.HostHeader));
                sb.Append("}");
            }
            return sb.Append("}").ToString();
        }

        public const string TunAddress = "172.19.0.1";
        /// Имя адаптера туннеля; меняется, если адаптер с этим именем остался от прошлого запуска.
        public static string TunName = "AngardTun";

        /// Конфигурация sing-box. Серверы идут тегами s0, s1, …; над ними группа «auto», которая сама выбирает
        /// самый быстрый по URL-тесту. tun == true — весь трафик компьютера идёт в туннель (нужны права
        /// администратора); false — локальный прокси на proxyPort, для проверок без прав.
        public static string Config(List<VpnServer> servers, int clashPort, bool tun, int proxyPort)
        {
            return Config(servers, clashPort, tun, proxyPort, null);
        }

        /// defaultInterface — сетевая карта для связи с серверами (обход чужого VPN); null — sing-box выбирает сам.
        public static string Config(List<VpnServer> servers, int clashPort, bool tun, int proxyPort, string defaultInterface)
        {
            StringBuilder ob = new StringBuilder(), tags = new StringBuilder();
            for (int i = 0; i < servers.Count; i++)
            {
                if (i > 0) { ob.Append(",\n    "); tags.Append(","); }
                ob.Append(Outbound(servers[i].Vless, "s" + i, servers[i].Ip));
                tags.Append("\"s" + i + "\"");
            }
            string inbound = tun
                ? "{\"type\":\"tun\",\"tag\":\"tun-in\",\"interface_name\":" + J(TunName) + ",\"address\":[\"" + TunAddress + "/30\"],\"mtu\":1400,\"auto_route\":true,\"strict_route\":true,\"stack\":\"mixed\"}"
                : "{\"type\":\"mixed\",\"tag\":\"mixed-in\",\"listen\":\"127.0.0.1\",\"listen_port\":" + proxyPort.ToString(CultureInfo.InvariantCulture) + "}";
            return "{\n" +
                "  \"log\": {\"level\":\"info\",\"timestamp\":true},\n" +
                "  \"dns\": {\"servers\":[{\"type\":\"https\",\"tag\":\"remote\",\"server\":\"1.1.1.1\",\"detour\":\"auto\"},{\"type\":\"local\",\"tag\":\"local\"}],\"final\":\"remote\",\"strategy\":\"ipv4_only\"},\n" +
                "  \"inbounds\": [" + inbound + "],\n" +
                "  \"outbounds\": [\n    " + ob + ",\n" +
                "    {\"type\":\"urltest\",\"tag\":\"auto\",\"outbounds\":[" + tags + "],\"url\":\"https://www.gstatic.com/generate_204\",\"interval\":\"3m\",\"tolerance\":250},\n" +
                // Группа уже упорядочена по скорости (с поправкой на пинг): большой допуск держит этот порядок,
                // и автовыбор уходит на сервер ниже в списке, только если у верхнего пинг хуже на четверть секунды или он упал.
                "    {\"type\":\"direct\",\"tag\":\"direct\"}\n  ],\n" +
                "  \"route\": {" + (defaultInterface != null ? "\"default_interface\":" + J(defaultInterface) + "," : "") + "\"auto_detect_interface\":true,\"default_domain_resolver\":\"local\",\"final\":\"auto\",\"rules\":[{\"action\":\"sniff\"},{\"protocol\":\"dns\",\"action\":\"hijack-dns\"},{\"ip_is_private\":true,\"outbound\":\"direct\"}" +
                // QUIC (UDP 443) внутри TCP-туннеля работает плохо: браузеры и YouTube без него сразу идут по TCP.
                (tun ? ",{\"network\":\"udp\",\"port\":443,\"action\":\"reject\"}" : "") + "]},\n" +
                "  \"experimental\": {\"clash_api\":{\"external_controller\":\"127.0.0.1:" + clashPort.ToString(CultureInfo.InvariantCulture) + "\"}}\n" +
                "}\n";
        }

        /// Ссылки → серверы для общего списка. Имена серверов не разрешаются здесь (это делает Load).
        public static List<VpnServer> ToServers(List<string> lines)
        {
            List<VpnServer> res = new List<VpnServer>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string line in lines)
            {
                VlessLink l = Parse(line);
                if (l == null || !seen.Add(l.Host + ":" + l.Port + "/" + l.Uuid + "/" + l.Sni + l.Path)) continue;
                VpnServer s = new VpnServer();
                s.Vless = l;
                s.Source = l.Label;
                s.HostName = "vless-" + res.Count + "-" + l.Host;
                s.CountryShort = CountryFromName(l.Name) ?? "??";
                s.CountryLong = CleanName(l.Name);
                if (s.CountryLong == "") s.CountryLong = l.Host;
                s.Ip = "";
                res.Add(s);
            }
            return res;
        }

        // ---------- общедоступные серверы (репозиторий Au1rxx/free-vpn-subscriptions) ----------

        // Один и тот же репозиторий по двум адресам: если GitHub недоступен напрямую, часто открывается CDN jsDelivr.
        static readonly string[] FreeBases = {
            "https://github.com/Au1rxx/free-vpn-subscriptions/raw/main/output/by-country/v2ray-base64-",
            "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/by-country/v2ray-base64-",
            "https://cdn.jsdelivr.net/gh/Au1rxx/free-vpn-subscriptions@main/output/by-country/v2ray-base64-",
            "https://testingcf.jsdelivr.net/gh/Au1rxx/free-vpn-subscriptions@main/output/by-country/v2ray-base64-",
            "https://fastly.jsdelivr.net/gh/Au1rxx/free-vpn-subscriptions@main/output/by-country/v2ray-base64-" };

        /// Удалось ли последнее обновление общедоступных серверов с сети (false — взята сохранённая копия).
        public static bool LastFreeOk;

        /// Первый из адресов, который отвечает; null — не отвечает ни один. reachable проверяет один адрес.
        /// Первый по порядку отвечающий адрес. Адреса проверяются одновременно: по очереди при плохой связи
        /// это занимало минуты (пять адресов, три прохода), и загрузка списка казалась зависшей.
        public static string ChooseBase(string[] bases, Func<string, bool> reachable)
        {
            int[] st = new int[bases.Length];      // 0 — ждём ответа, 1 — отвечает, 2 — нет
            for (int i = 0; i < bases.Length; i++)
            {
                int k = i;
                System.Threading.Thread th = new System.Threading.Thread(delegate()
                {
                    bool ok = false;
                    try { ok = reachable(bases[k]); } catch (Exception) { }
                    System.Threading.Thread.VolatileWrite(ref st[k], ok ? 1 : 2);
                });
                th.IsBackground = true;
                th.Start();
            }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                bool pending = false;
                for (int i = 0; i < bases.Length; i++)
                {
                    int v = System.Threading.Thread.VolatileRead(ref st[i]);
                    if (v == 1 && !pending) return bases[i];      // все, кто важнее, уже ответили «нет»
                    if (v == 0) pending = true;
                }
                if (!pending) return null;
                if (sw.ElapsedMilliseconds > 15000) break;
                System.Threading.Thread.Sleep(30);
            }
            for (int i = 0; i < bases.Length; i++) if (System.Threading.Thread.VolatileRead(ref st[i]) == 1) return bases[i];
            return null;
        }
        const string FreeIndex = "https://github.com/Au1rxx/free-vpn-subscriptions/tree/main/output/by-country";
        static readonly string[] FreeFallback = { "AE", "AL", "AT", "AU", "BY", "CA", "CH", "CL", "DE", "DK", "EE", "ES", "FI", "FR", "GB", "HK", "IL",
            "IN", "IT", "JP", "KR", "LV", "MX", "NL", "PL", "RO", "SE", "SG", "TH", "TR", "TW", "US", "VN", "ZA" };

        /// Коды стран, для которых в репозитории есть файл подписки.
        public static List<string> ParseFreeCountries(string page)
        {
            List<string> res = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(page ?? "", @"v2ray-base64-([A-Z]{2})\.txt"))
                if (!res.Contains(m.Groups[1].Value)) res.Add(m.Groups[1].Value);
            return res;
        }

        public static void ResolvePublic(VpnServer s) { Resolve(s); }

        static void Resolve(VpnServer s)
        {
            IPAddress ip;
            if (IPAddress.TryParse(s.Vless.Host, out ip)) { s.Ip = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? s.Vless.Host : ""; return; }
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(s.Vless.Host))
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { s.Ip = a.ToString(); break; }
            }
            catch (Exception) { }
        }

        /// Общедоступные серверы VLESS по странам. Страна берётся из имени файла: в названиях серверов её нет.
        /// При неудаче загрузки возвращает сохранённую копию. note — краткий итог для журнала.
        /// Готовые списки бесплатных серверов, которые их авторы сами регулярно проверяют (адреса raw.githubusercontent.com).
        /// Отобраны замером скорости из Минска (build\live\bench.txt). Страна — по флагу в названии сервера.
        public static readonly string[] FlatSources = {
            "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/BLACK_VLESS_RUS.txt",
            "https://raw.githubusercontent.com/roosterkid/openproxylist/main/V2RAY_RAW.txt",
            "https://raw.githubusercontent.com/4n0nymou3/multi-proxy-config-fetcher/main/configs/proxy_configs.txt",
            "https://raw.githubusercontent.com/mahdibland/V2RayAggregator/master/Eternity.txt",
            "https://raw.githubusercontent.com/Danialsamadi/v2go/main/AllConfigsSub.txt",
            "https://raw.githubusercontent.com/zieng2/wl/main/vless_universal.txt",
            "https://raw.githubusercontent.com/sakha1370/OpenRay/main/output/all_valid_proxies.txt",
        };
        // Выбор источников — по замеру из Минска (build\live\bench.txt): годных по скорости из проверенных.
        // Убраны igareck BLACK_SS+All (0 годных) и BLACK_VLESS_RUS_mobile (2 из 41).
        const int PerSourceMax = 700;      // больше из одного списка не берётся: огромные сборники забили бы весь замер

        /// Тот же файл по нескольким адресам: GitHub напрямую, CDN jsDelivr и его зеркало на Cloudflare.
        public static List<string> Mirrors(string raw)
        {
            List<string> res = new List<string>();
            res.Add(raw);
            const string pre = "https://raw.githubusercontent.com/";
            if (raw.StartsWith(pre, StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = raw.Substring(pre.Length).Split(new char[] { '/' }, 4);
                if (parts.Length == 4)
                {
                    string tail = parts[0] + "/" + parts[1] + "@" + parts[2] + "/" + parts[3];
                    res.Add("https://cdn.jsdelivr.net/gh/" + tail);
                    res.Add("https://testingcf.jsdelivr.net/gh/" + tail);
                }
            }
            return res;
        }

        /// Строки готового списка → "CC|ссылка" (CC — по флагу в имени, иначе "??"); комментарии и чужие протоколы отбрасываются.
        public static List<string> FlatLines(string body, int max)
        {
            List<string> res = new List<string>();
            foreach (string line in SplitSubscription(body))
            {
                if (line.StartsWith("#") || !IsLink(line)) continue;
                VlessLink l = Parse(line);
                if (l == null) continue;
                res.Add((CountryFromName(l.Name) ?? "??") + "|" + line);
                if (res.Count >= max) break;
            }
            return res;
        }

        /// Отметки этапов загрузки для журнала приложения (null — не нужны).
        public static Action<string> Trace;

        public static List<VpnServer> LoadFree(out string note)
        {
            // Другой VPN (не наш туннель) часто режет GitHub: тогда сразу качаем напрямую через сетевую карту.
            bindIp = null;
            foreach (string o in Tunnels.Active()) if (!o.StartsWith("AngardTun", StringComparison.Ordinal)) { bindIp = Tunnels.PhysicalIp(); break; }
            System.Diagnostics.Stopwatch lw = System.Diagnostics.Stopwatch.StartNew();
            List<string> countries;
            try { countries = ParseFreeCountries(Fetch(FreeIndex, 6000)); }
            catch (Exception) { countries = new List<string>(); }
            if (countries.Count == 0) countries = new List<string>(FreeFallback);
            if (Trace != null) Trace("Списки VLESS: перечень стран за " + lw.ElapsedMilliseconds / 1000 + " с");

            Func<string, bool> reach = delegate(string b) { try { Fetch(b + "NL.txt", 6000); return true; } catch (Exception) { return false; } };
            string baseUrl = ChooseBase(FreeBases, reach);
            if (baseUrl == null && bindIp != null) { bindIp = null; baseUrl = ChooseBase(FreeBases, reach); }
            if (baseUrl == null)
            {
                // Не открылся ни один адрес: возможно, мешает другой VPN — пробуем напрямую через сетевую карту.
                bindIp = Tunnels.PhysicalIp();
                if (bindIp != null) baseUrl = ChooseBase(FreeBases, reach);
                if (baseUrl == null) bindIp = null;
            }
            if (Trace != null) Trace("Списки VLESS: выбран адрес " + (baseUrl ?? "нет") + " за " + lw.ElapsedMilliseconds / 1000 + " с");
            // Общий предел на все загрузки: при «тормозящем» адресе ожидания потоков складывались в десятки минут,
            // и приложение висело на загрузке списка. Что не успело за это время — пропускается.
            DateTime deadline = DateTime.Now.AddSeconds(45);
            Dictionary<string, string> bodies = new Dictionary<string, string>();
            int next = 0;
            List<System.Threading.Thread> threads = new List<System.Threading.Thread>();
            for (int t = 0; t < 8; t++)
            {
                System.Threading.Thread th = new System.Threading.Thread(delegate()
                {
                    while (true)
                    {
                        int i = System.Threading.Interlocked.Increment(ref next) - 1;
                        if (i >= countries.Count) return;
                        try
                        {
                            if (baseUrl == null) return;
                            // Связь с источником бывает рваной: до трёх попыток на страну.
                            for (int tr = 0; tr < 3 && DateTime.Now < deadline; tr++)
                            {
                                try
                                {
                                    string body = Fetch(baseUrl + countries[i] + ".txt", 15000);
                                    lock (bodies) bodies[countries[i]] = body;
                                    break;
                                }
                                catch (WebException e)
                                {
                                    HttpWebResponse r = e.Response as HttpWebResponse;
                                    if (r != null && r.StatusCode == HttpStatusCode.NotFound) break;
                                }
                            }
                        }
                        catch (Exception) { }
                    }
                });
                th.IsBackground = true;
                th.Start();
                threads.Add(th);
            }
            // Остальные источники — одновременно с первым.
            Dictionary<int, string> flat = new Dictionary<int, string>();
            int nf = 0;
            for (int t = 0; t < 6; t++)
            {
                System.Threading.Thread th = new System.Threading.Thread(delegate()
                {
                    while (true)
                    {
                        int i = System.Threading.Interlocked.Increment(ref nf) - 1;
                        if (i >= FlatSources.Length) return;
                        foreach (string u in Mirrors(FlatSources[i]))
                        {
                            if (DateTime.Now > deadline) break;
                            try { string body = Fetch(u, 20000); lock (flat) flat[i] = body; break; }
                            catch (Exception) { }
                        }
                    }
                });
                th.IsBackground = true;
                th.Start();
                threads.Add(th);
            }
            foreach (System.Threading.Thread th in threads) th.Join(Math.Max(0, (int)(deadline - DateTime.Now).TotalMilliseconds + 3000));

            List<string> cache = new List<string>();
            lock (bodies)
                foreach (KeyValuePair<string, string> p in bodies)
                    foreach (string line in SplitSubscription(p.Value))
                        if (IsLink(line)) cache.Add(p.Key + "|" + line);
            int fromAu = cache.Count;
            StringBuilder perSource = new StringBuilder();
            lock (flat)
                for (int i = 0; i < FlatSources.Length; i++)
                {
                    string body;
                    List<string> got = flat.TryGetValue(i, out body) ? FlatLines(body, PerSourceMax) : new List<string>();
                    cache.AddRange(got);
                    string name = FlatSources[i].Substring(FlatSources[i].LastIndexOf('/') + 1);
                    perSource.Append(", " + name + " " + (flat.ContainsKey(i) ? got.Count.ToString() : "нет связи"));
                }
            bool fromCache = cache.Count == 0;
            LastFreeOk = !fromCache;
            try
            {
                if (fromCache) { if (File.Exists(Paths.FreeCache)) cache = new List<string>(File.ReadAllLines(Paths.FreeCache, Encoding.UTF8)); }
                else File.WriteAllLines(Paths.FreeCache, cache.ToArray(), new UTF8Encoding(false));
            }
            catch (IOException) { }

            List<VpnServer> res = FreeServers(cache);
            if (Trace != null) Trace("Списки VLESS: скачано за " + lw.ElapsedMilliseconds / 1000 + " с, строк " + cache.Count);
            // Имена серверов → адреса, в несколько потоков (серверов сотни).
            int idx = 0;
            threads.Clear();
            for (int t = 0; t < 16; t++)
            {
                System.Threading.Thread th = new System.Threading.Thread(delegate()
                {
                    while (true)
                    {
                        int i = System.Threading.Interlocked.Increment(ref idx) - 1;
                        if (i >= res.Count) return;
                        Resolve(res[i]);
                    }
                });
                th.IsBackground = true;
                th.Start();
                threads.Add(th);
            }
            DateTime resolveBy = DateTime.Now.AddSeconds(30);
            foreach (System.Threading.Thread th in threads) th.Join(Math.Max(0, (int)(resolveBy - DateTime.Now).TotalMilliseconds));
            if (Trace != null) Trace("Списки VLESS: адреса серверов за " + lw.ElapsedMilliseconds / 1000 + " с");
            GeoFill(res);
            IPAddress usedBind = bindIp;
            bindIp = null;
            note = (usedBind != null ? "мимо чужого VPN через " + usedBind + ", " : "") + "стран " + countries.Count + ", Au1rxx " + fromAu + perSource + "; всего серверов " + res.Count + (fromCache ? " (сохранённая копия: источник недоступен)" : " (с " + (baseUrl ?? "").Substring(0, Math.Min(28, (baseUrl ?? "").Length)) + ")");
            return res;
        }

        /// Страна серверов без флага в названии ("??") — по IP-адресу через ip-api.com (пакетами по 100, результат хранится в geo.txt).
        static void GeoFill(List<VpnServer> list)
        {
            string file = Path.Combine(Paths.DataDir, "geo.txt");
            Dictionary<string, string> geo = new Dictionary<string, string>();
            try { if (File.Exists(file)) foreach (string l in File.ReadAllLines(file)) { int b = l.IndexOf('|'); if (b > 0) geo[l.Substring(0, b)] = l.Substring(b + 1); } }
            catch (IOException) { }
            List<string> ask = new List<string>();
            foreach (VpnServer s in list)
                if (s.CountryShort == "??" && !string.IsNullOrEmpty(s.Ip) && !geo.ContainsKey(s.Ip) && !ask.Contains(s.Ip)) ask.Add(s.Ip);
            bool changed = false;
            System.Diagnostics.Stopwatch gw = System.Diagnostics.Stopwatch.StartNew();
            for (int k = 0; k < ask.Count && k < 400 && gw.ElapsedMilliseconds < 20000; k += 100)      // ip-api: не больше 15 запросов в минуту
            {
                try
                {
                    StringBuilder body = new StringBuilder("[");
                    for (int i = k; i < Math.Min(ask.Count, k + 100); i++) body.Append(i > k ? "," : "").Append(J(ask[i]));
                    body.Append("]");
                    HttpWebRequest r = (HttpWebRequest)WebRequest.Create("http://ip-api.com/batch?fields=countryCode,query");
                    r.Method = "POST"; r.ContentType = "application/json"; r.Timeout = 10000; r.ReadWriteTimeout = 10000;
                    IPAddress bind = bindIp;
                    if (bind != null) r.ServicePoint.BindIPEndPointDelegate = Http.Bind(bind);
                    byte[] data = Encoding.UTF8.GetBytes(body.ToString());
                    using (Stream st = r.GetRequestStream()) st.Write(data, 0, data.Length);
                    string ans;
                    using (WebResponse resp = r.GetResponse()) ans = Http.ReadAll(resp, 15000);
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ans, "\\{[^}]*\\}"))
                    {
                        string cc = JStr(m.Value, "countryCode"), ip = JStr(m.Value, "query");
                        if (ip != "") { geo[ip] = cc.Length == 2 ? cc.ToUpperInvariant() : "??"; changed = true; }
                    }
                }
                catch (Exception) { break; }
            }
            foreach (VpnServer s in list)
            {
                string cc;
                if (s.CountryShort == "??" && !string.IsNullOrEmpty(s.Ip) && geo.TryGetValue(s.Ip, out cc) && cc != "??") { s.CountryShort = cc; s.CountryLong = cc; }
            }
            if (changed)
                try
                {
                    List<string> lines = new List<string>();
                    foreach (KeyValuePair<string, string> p in geo) lines.Add(p.Key + "|" + p.Value);
                    if (lines.Count > 20000) lines.RemoveRange(0, lines.Count - 20000);
                    File.WriteAllLines(file, lines.ToArray());
                }
                catch (IOException) { }
        }

        /// Строки "CC|vless://…" → серверы. Повторы одного и того же сервера в разных файлах отбрасываются.
        public static List<VpnServer> FreeServers(List<string> lines)
        {
            List<VpnServer> res = new List<VpnServer>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string line in lines)
            {
                int bar = line.IndexOf('|');
                if (bar != 2) continue;
                VlessLink l = Parse(line.Substring(3));
                if (l == null || !seen.Add(l.Proto + "/" + l.Host + ":" + l.Port + "/" + l.Uuid)) continue;
                VpnServer s = new VpnServer();
                s.Vless = l;
                s.Source = l.Label;
                s.CountryShort = line.Substring(0, 2).ToUpperInvariant();
                s.CountryLong = s.CountryShort;
                s.HostName = "free-" + s.CountryShort + "-" + res.Count;
                s.Ip = "";
                res.Add(s);
            }
            return res;
        }

        static string Fetch(string url)
        {
            return Fetch(url, 20000);
        }

        /// Адрес сетевой карты, с которого качать список мимо чужого VPN (тот, например, режет GitHub); null — как обычно.
        static volatile IPAddress bindIp;

        static string Fetch(string url, int timeoutMs)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }   // TLS 1.2
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            IPAddress bind = bindIp;
            if (bind != null) r.ServicePoint.BindIPEndPointDelegate = Http.Bind(bind);
            else r.ServicePoint.BindIPEndPointDelegate = null;
            r.Timeout = timeoutMs;
            r.ReadWriteTimeout = timeoutMs;
            r.UserAgent = "v2rayN/7.10";
            using (WebResponse resp = r.GetResponse())
                return Http.ReadAll(resp, timeoutMs * 2);
        }

        /// Загружает подписку (адрес https://… либо сами ссылки vless://…). При неудаче берёт сохранённую копию.
        /// error — что пошло не так, либо null.
        public static List<VpnServer> Load(string subscription, out string error)
        {
            error = null;
            string sub = (subscription ?? "").Trim();
            if (sub == "") return new List<VpnServer>();
            string body = null;
            if (IsLink(sub)) body = sub;
            else
            {
                try
                {
                    body = Fetch(sub);
                    try { File.WriteAllText(Paths.SubCache, body, new UTF8Encoding(false)); } catch (IOException) { }
                }
                catch (Exception e)
                {
                    error = "подписка не загрузилась: " + e.Message;
                    try { if (File.Exists(Paths.SubCache)) body = File.ReadAllText(Paths.SubCache, Encoding.UTF8); } catch (IOException) { }
                }
            }
            if (body == null) return new List<VpnServer>();
            List<string> lines = SplitSubscription(body);
            List<VpnServer> res = ToServers(lines);
            if (res.Count == 0 && error == null)
                error = lines.Count == 0 ? "подписка пуста" : "в подписке нет серверов VLESS поддерживаемого вида (строк: " + lines.Count + ")";
            foreach (VpnServer s in res)
            {
                IPAddress ip;
                if (IPAddress.TryParse(s.Vless.Host, out ip)) { s.Ip = s.Vless.Host; continue; }
                try
                {
                    foreach (IPAddress a in Dns.GetHostAddresses(s.Vless.Host))
                        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { s.Ip = a.ToString(); break; }
                }
                catch (Exception) { }
            }
            return res;
        }
    }
}
