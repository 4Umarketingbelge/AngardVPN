# Упаковка AngardVPN: установщик (один exe) и переносимый zip. Результат — папка dist.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$pkg = Join-Path $root 'build\package'
$dist = Join-Path $root 'dist'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# 1. Свежая сборка приложения и тесты
& "$root\build.ps1"
$ver = [regex]::Match((Get-Content "$root\src\MainForm.cs" -Raw -Encoding UTF8), 'Version = "([\d\.]+)"').Groups[1].Value
if (-not $ver) { throw 'Не найдена версия в MainForm.cs' }
"Версия: $ver"

# 2. Состав пакета
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
$stage = Join-Path $pkg 'stage'
New-Item -ItemType Directory -Force "$stage\engine", "$stage\licenses" | Out-Null
Copy-Item "$root\AngardVPN.exe" $stage
Copy-Item "$root\engine\sing-box.exe", "$root\engine\libcronet.dll" "$stage\engine"
$lic = Get-ChildItem "$root\build\singbox-dl" -Recurse -Filter LICENSE | Select-Object -First 1
if (-not $lic) { throw 'Не найдена лицензия sing-box' }
Copy-Item $lic.FullName "$stage\licenses\sing-box-LICENSE.txt"
@'
AngardVPN: сторонние компоненты

sing-box (engine\sing-box.exe, libcronet.dll) — https://github.com/SagerNet/sing-box
  Лицензия: GPL-3.0 с дополнительным условием об использовании названия, текст — sing-box-LICENSE.txt.
  Поставляется без изменений. Исходный код: https://github.com/SagerNet/sing-box

OpenVPN (устанавливается из установщика, если вы это выбрали) — https://openvpn.net
  Лицензия: GPL-2.0. Поставляется без изменений, официальный установщик OpenVPN Inc.
  Исходный код: https://github.com/OpenVPN/openvpn

Серверы: VPN Gate (Университет Цукубы, https://www.vpngate.net) и открытый список
  https://github.com/Au1rxx/free-vpn-subscriptions. Эти серверы не принадлежат AngardVPN.
'@ | Set-Content "$stage\licenses\NOTICE.txt" -Encoding UTF8
@"
AngardVPN $ver

Что это
  Небольшой VPN-клиент для Windows 10 и 11: одна кнопка, много серверов, сам выбирает быстрый.
  Серверы бесплатные и общедоступные (VLESS и VPN Gate), обновляются автоматически.

Запуск
  Запустите AngardVPN.exe (нужны права администратора: Windows спросит подтверждение).
  Нажмите большую кнопку со щитом. Внизу можно выбрать страну, справа от неё — обновить список и настройки.
  Серверы VLESS работают сразу. Для серверов VPN Gate нужен OpenVPN (openvpn.net): его ставит установщик.
  Кнопки в шапке окна: «свернуть в строку» (останутся кнопка, страна и скорость) и булавка
  «поверх всех окон» (окно не прячется за другими, пока VPN подключён).

Важно знать
  Серверы общедоступные: их владельцы могут видеть незашифрованный трафик. Не входите через них в банки
  и не вводите важные пароли без https.
  Не включайте одновременно с другим VPN: они мешают друг другу.
  Если не подключается: в нижней части окна есть кнопка «Создать отчёт» (шестерёнка, затем «Создать отчёт»).

Нужно: Windows 10 1903 или новее / Windows 11, .NET Framework 4.8 (уже есть в системе).
Лицензии сторонних компонентов — в папке licenses.
"@ | Set-Content "$stage\README.txt" -Encoding UTF8

# 3. payload.zip (пути с прямыми косыми: так читает установщик)
$payload = Join-Path $pkg 'payload.zip'
function New-Zip([string]$zipPath, [string]$prefix) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $z = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        Get-ChildItem $stage -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, $prefix + $rel, 'Optimal')
        }
    } finally { $z.Dispose() }
}
New-Zip $payload ''

# 4. Установщик OpenVPN (подпись проверяется)
$msi = Join-Path $pkg 'openvpn.msi'
$cached = Get-ChildItem $env:TEMP -Recurse -Filter 'OpenVPN-*-amd64.msi' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cached) { Copy-Item $cached.FullName $msi }
else {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest 'https://swupdate.openvpn.org/community/releases/OpenVPN-2.7.9-I001-amd64.msi' -OutFile $msi -UseBasicParsing
}
$sig = Get-AuthenticodeSignature $msi
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'OpenVPN Inc') { throw "Подпись OpenVPN не прошла проверку: $($sig.Status)" }

# 5. Сборка установщика
Set-Content "$pkg\SetupVersion.cs" -Value "static class SetupVer { public const string V = `"$ver`"; }" -Encoding UTF8
New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Join-Path $dist "AngardVPN-Setup-$ver.exe"
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /nowarn:0649 /platform:anycpu `
    "/out:$setup" "/win32manifest:$root\src\setup.manifest" "/win32icon:$root\build\app.ico" `
    "/resource:$payload,payload.zip" "/resource:$msi,openvpn.msi" `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.CSharp.dll `
    "$root\src\Setup.cs" "$pkg\SetupVersion.cs" "$root\src\Core.cs" "$root\src\Vless.cs" "$root\src\Controls.cs"
if ($LASTEXITCODE -ne 0) { throw 'Установщик не собрался' }

# 6. Переносимая версия: распаковать куда угодно и запустить AngardVPN.exe
$portable = Join-Path $dist "AngardVPN-$ver-portable.zip"
New-Zip $portable 'AngardVPN/'

Get-ChildItem $dist -File | ForEach-Object {
    "{0}  {1:N1} МБ  SHA256 {2}" -f $_.Name, ($_.Length / 1MB), (Get-FileHash $_.FullName -Algorithm SHA256).Hash
}
