# Стабильность лучших серверов по отдельности: несколько кругов, в каждом пинг (повторные запросы)
# и скачивание 10 МБ с двух разных площадок. Запуск: после ChainCheck.exe (он пишет multi-test.json и multi-ports.txt).
param([int]$Rounds = 3, [string]$Exe = 'E:\01\engine\sing-box.exe')
$dir = Join-Path $env:LOCALAPPDATA 'AngardVPN'
$cfg = Join-Path $dir 'multi-test.json'
$lines = Get-Content (Join-Path $dir 'multi-ports.txt') -Encoding UTF8 | ? { $_ }
$log = Join-Path $env:TEMP 'stability.log'
$p = Start-Process $Exe -ArgumentList "run --disable-color -c `"$cfg`"" -RedirectStandardError $log -RedirectStandardOutput "$log.out" -PassThru -WindowStyle Hidden
Start-Sleep 4
$inv = [Globalization.CultureInfo]::InvariantCulture
$urls = 'http://cachefly.cachefly.net/10mb.test', 'https://proof.ovh.net/files/10Mb.dat'
$res = @{}
try {
    foreach ($r in 1..$Rounds) {
        foreach ($l in $lines) {
            $port = ($l -split ' ')[0]; $px = "http://127.0.0.1:$port"
            $u = 'https://www.gstatic.com/generate_204'
            $t = @(curl.exe -s -o NUL -x $px -m 20 -w "%{time_total}\n" $u $u $u $u 2>$null | % { [int]([double]::Parse($_, $inv) * 1000) })
            $ping = if ($t.Count -ge 2) { ($t[1..($t.Count - 1)] | Measure -Minimum).Minimum } else { -1 }
            $sp = foreach ($d in $urls) {
                $a = (curl.exe -s -o NUL -x $px -m 15 -w "%{size_download} %{speed_download}" $d) -split ' '
                if ([double]$a[0] -gt 1e6) { [math]::Round([double]::Parse($a[1], $inv) * 8 / 1e6, 1) } else { 0 }
            }
            if (-not $res[$l]) { $res[$l] = New-Object System.Collections.ArrayList }
            [void]$res[$l].Add("пинг $ping / $($sp -join ' и ') Мбит/с")
        }
    }
}
finally { Stop-Process $p -Force -ErrorAction SilentlyContinue }
foreach ($l in $lines) { ($l -split ' ', 2)[1]; $res[$l] | % { "    $_" } }
