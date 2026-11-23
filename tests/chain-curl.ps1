# Проверка полной цепочки через curl: запускает sing-box с данным конфигом (вход — локальный прокси)
# и меряет пинг (повторные запросы по открытому соединению), скорость и открытие сайтов.
param([string]$Config, [string]$Exe = 'E:\01\engine\sing-box.exe', [int]$Clash = 0)
$ErrorActionPreference = 'Continue'
$j = [IO.File]::ReadAllText($Config)
$port = [regex]::Match($j, '"listen_port":(\d+)').Groups[1].Value
if ($Clash -eq 0) { $Clash = [int][regex]::Match($j, '127\.0\.0\.1:(\d+)').Groups[1].Value }
$log = Join-Path $env:TEMP 'chain-curl.log'
$p = Start-Process $Exe -ArgumentList "run --disable-color -c `"$Config`"" -RedirectStandardError $log -RedirectStandardOutput "$log.out" -PassThru -WindowStyle Hidden
Start-Sleep 9
try {
    if ($p.HasExited) { "sing-box завершился"; Get-Content $log -Tail 5; return }
    try { "автовыбор: " + (Invoke-RestMethod "http://127.0.0.1:$Clash/proxies/auto" -TimeoutSec 3).now } catch { "автовыбор: ?" }
    $px = "http://127.0.0.1:$port"
    # Пять запросов подряд по одному соединению: первый с рукопожатиями, остальные — чистый пинг.
    $u = 'https://www.gstatic.com/generate_204'
    $t = curl.exe -s -o NUL -x $px -m 30 -w "%{time_total}\n" $u $u $u $u $u 2>$null
    "пинг через цепочку, мс: " + (($t | % { [int]([double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) * 1000) }) -join ', ')
    $dl = 'http://cachefly.cachefly.net/10mb.test', 'http://ipv4.download.thinkbroadband.com/10MB.zip', 'https://proof.ovh.net/files/10Mb.dat'
    foreach ($k in 0..2) {
        $r = curl.exe -s -o NUL -x $px -m 30 -w "%{size_download} %{speed_download}" $dl[$k]
        $a = $r -split ' '
        "скорость, $(([uri]$dl[$k]).Host): {0:N1} Мбит/с ({1:N0} МБ)" -f ([double]::Parse($a[1], [Globalization.CultureInfo]::InvariantCulture) * 8 / 1e6), ([double]$a[0] / 1e6)
    }
    foreach ($site in 'https://www.youtube.com/', 'https://www.instagram.com/', 'https://web.telegram.org/', 'https://www.google.com/', 'https://author.today/', 'https://chatgpt.com/') {
        $r = curl.exe -s -o NUL -x $px -m 20 -A 'Mozilla/5.0 Chrome/130' -w "%{http_code} %{time_total}" $site
        $a = $r -split ' '
        "  $site → " + ($(if ($a[0] -ne '000') { "HTTP $($a[0]), {0:N0} мс" -f ([double]::Parse($a[1], [Globalization.CultureInfo]::InvariantCulture) * 1000) } else { 'НЕ ОТКРЫЛСЯ' }))
    }
    "внешний IP: " + (curl.exe -s -x $px -m 15 https://api.ipify.org)
    try { "автовыбор сейчас: " + (Invoke-RestMethod "http://127.0.0.1:$Clash/proxies/auto" -TimeoutSec 3).now } catch { }
}
finally { Stop-Process $p -Force -ErrorAction SilentlyContinue }
$e = Select-String -Path $log -Pattern 'FATAL|ERROR' | Select -First 3
if ($e) { "ошибки ядра:"; $e | % { '  ' + $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) } }
