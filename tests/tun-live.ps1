# Живая проверка режима адаптера (TUN): запускает AngardVPN.exe --autoconnect, ждёт подключения,
# меряет пинг, скорость и сайты обычным curl без прокси (то есть через системный туннель), затем выключает.
# Запуск с правами администратора. Сам себя завершает не позже чем через $Limit секунд.
param([string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'build\live\tun-live.txt'), [int]$Limit = 480, [int]$Hold = 0, [string]$Nic = '')
$root = Split-Path -Parent $PSScriptRoot
$ErrorActionPreference = 'Continue'
$inv = [Globalization.CultureInfo]::InvariantCulture
$dir = Join-Path $env:LOCALAPPDATA 'AngardVPN'
$t0 = Get-Date
function W($s) { $l = ('[{0,5:N0} с] ' -f ((Get-Date) - $t0).TotalSeconds) + $s; Add-Content -Path $Out -Value $l -Encoding UTF8 }
function Stop-All {
    # Сначала мягко, как при выходе из меню: приложение само снимает туннель, адаптер и правила.
    if (Get-Process AngardVPN -ErrorAction SilentlyContinue) {
        Start-Process "$root\AngardVPN.exe" -ArgumentList '--quit'
        $w = 0; while ((Get-Process AngardVPN -ErrorAction SilentlyContinue) -and $w -lt 30) { Start-Sleep -Milliseconds 500; $w++ }
        if (Get-Process AngardVPN -ErrorAction SilentlyContinue) { W 'мягкий выход не сработал за 15 с, завершаю принудительно' } else { W "приложение вышло само за $($w / 2) с" }
    }
    Get-Process AngardVPN -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-CimInstance Win32_Process -Filter "Name='sing-box.exe'" | ? { $_.CommandLine -like '*singbox.json*' } | % { Stop-Process -Id $_.ProcessId -Force }
    Get-Process openvpn -ErrorAction SilentlyContinue | ? { $_.Path -like "$root*" -or $_.Path -like '*OpenVPN*' } | Stop-Process -Force
    foreach ($n in 'AngardVPN KillSwitch A', 'AngardVPN KillSwitch B', 'AngardVPN IPv6') { netsh advfirewall firewall delete rule name="$n" | Out-Null }
}
Set-Content -Path $Out -Value "Живая проверка TUN $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), админ: $(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators'))" -Encoding UTF8
try {
    W ("до подключения: внешний IP " + (curl.exe -s -m 10 https://api.ipify.org))
    Stop-All
    Remove-Item (Join-Path $dir 'state.txt') -ErrorAction SilentlyContinue
    $app = Start-Process "$root\AngardVPN.exe" -ArgumentList '--autoconnect', '--tray' -PassThru
    W "запущен AngardVPN, pid $($app.Id)"
    $on = $false; $last = ''
    while (((Get-Date) - $t0).TotalSeconds -lt ($Limit - 150)) {
        Start-Sleep 2
        $st = Get-Content (Join-Path $dir 'state.txt') -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($st -and $st -ne $last) { W "состояние: $st"; $last = $st }
        if ($st -like 'On|*') { $on = $true; break }
        if ($app.HasExited) { W 'приложение закрылось'; break }
    }
    if ($on) {
        Start-Sleep 3
        W ("внешний IP через туннель: " + (curl.exe -s -m 15 https://api.ipify.org))
        W ("страна по IP: " + (curl.exe -s -m 15 'http://ip-api.com/line/?fields=country,city,isp'  | Out-String).Replace("`n", ' ').Trim())
        foreach ($k in 1..3) {
            $u = 'https://www.gstatic.com/generate_204'
            $t = @(curl.exe -s -o NUL -m 20 -w "%{time_total}\n" $u $u $u $u $u 2>$null | % { [int]([double]::Parse($_, $inv) * 1000) })
            W ("пинг (первый с рукопожатием, далее по открытому соединению), мс: " + ($t -join ', '))
            foreach ($d in 'http://cachefly.cachefly.net/10mb.test', 'https://proof.ovh.net/files/10Mb.dat', 'http://ipv4.download.thinkbroadband.com/10MB.zip') {
                $a = (curl.exe -s -o NUL -m 25 -w "%{size_download} %{speed_download}" $d) -split ' '
                W ("  скорость {0}: {1:N1} Мбит/с ({2:N1} МБ)" -f ([uri]$d).Host, ([double]::Parse($a[1], $inv) * 8 / 1e6), ([double]$a[0] / 1e6))
            }
        }
        # Разбивка по этапам: имя (DNS) / TCP / TLS / всего, мс — чтобы отличать задержки DNS от задержек сервера.
        foreach ($h in 'www.wikipedia.org', 'github.com', 'www.bbc.com', 'www.reddit.com', 'discord.com', 'www.twitch.tv') {
            $r = (curl.exe -s -o NUL -m 20 -w "%{time_namelookup} %{time_connect} %{time_appconnect} %{time_total} %{http_code}" "https://$h/") -split ' '
            W ("  этапы $h, мс: DNS {0:N0} / TCP {1:N0} / TLS {2:N0} / всего {3:N0}, HTTP {4}" -f ([double]::Parse($r[0], $inv) * 1000), ([double]::Parse($r[1], $inv) * 1000), ([double]::Parse($r[2], $inv) * 1000), ([double]::Parse($r[3], $inv) * 1000), $r[4])
        }
        foreach ($site in 'https://www.youtube.com/', 'https://www.instagram.com/', 'https://web.telegram.org/', 'https://www.google.com/', 'https://chatgpt.com/', 'https://author.today/', 'https://charter97.org/') {
            $a = (curl.exe -s -o NUL -m 20 -A 'Mozilla/5.0 Chrome/130' -w "%{http_code} %{time_total}" $site) -split ' '
            W ("  $site → " + $(if ($a[0] -ne '000') { "HTTP $($a[0]), {0:N0} мс" -f ([double]::Parse($a[1], $inv) * 1000) } else { 'НЕ ОТКРЫЛСЯ' }))
        }
        $dns = Resolve-DnsName www.youtube.com -ErrorAction SilentlyContinue | ? IPAddress | select -First 1
        W ("DNS youtube: " + $dns.IPAddress)
        if ($Hold -gt 0) { W "держу соединение $Hold с"; Start-Sleep $Hold; W ("после ожидания: " + (curl.exe -s -o NUL -m 15 -w "%{http_code} %{time_total}" https://www.google.com/)) }
    }
}
finally {
    Stop-All
    $jf = Join-Path $dir 'journal.txt'
    if (Test-Path $jf) { W 'журнал приложения за этот запуск:'; Get-Content $jf -Encoding UTF8 | ? { $_ -ge $t0.ToString('yyyy-MM-dd HH:mm:ss') } | % { Add-Content -Path $Out -Value "    $_" -Encoding UTF8 } }
    W 'AngardVPN остановлен, правила брандмауэра сняты'
    Start-Sleep 3
    if ($Nic) { W ("после выключения: напрямую через сетевую карту " + (curl.exe -s -m 10 --interface $Nic https://api.ipify.org) + ", обычным путём " + (curl.exe -s -m 10 https://api.ipify.org) + ", адаптеры: " + ((Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | ? Name -like 'Angard*' | % { $_.Name + '=' + $_.Status }) -join ', ')) }
    W 'ГОТОВО'
}
