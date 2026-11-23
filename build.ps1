# Сборка AngardVPN.exe и запуск тестов. Нужен только .NET Framework 4.8, встроенный в Windows 10.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null
$core = "$root\src\Core.cs", "$root\src\Report.cs", "$root\src\Vless.cs"
$app = $core + "$root\src\Engine.cs", "$root\src\SingBox.cs", "$root\src\KillSwitch.cs", "$root\src\Controls.cs", "$root\src\Hero.cs", "$root\src\Dialogs.cs", "$root\src\NetPanel.cs", "$root\src\MainForm.cs"

# --- тесты ---
& $csc /nologo /codepage:65001 /target:exe "/out:$out\Tests.exe" $core "$root\src\Engine.cs" "$root\src\SingBox.cs" "$root\tests\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Тесты не собрались' }
& "$out\Tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли' }

# --- значок (рисуется тем же кодом, что и значок в трее) ---
& $csc /nologo /codepage:65001 /nowarn:0649 /target:exe "/out:$out\IconGen.exe" "$root\src\Core.cs" "$root\src\Vless.cs" "$root\src\Controls.cs" "$root\tests\IconGen.cs"
if ($LASTEXITCODE -ne 0) { throw 'IconGen не собрался' }
& "$out\IconGen.exe" $out
if ($LASTEXITCODE -ne 0) { throw 'Значок не создан' }

# --- вспомогательные проверки ---
& $csc /nologo /codepage:65001 /target:exe "/out:$out\Probe.exe" $core "$root\src\Engine.cs" "$root\src\SingBox.cs" "$root\tests\Probe.cs"
if ($LASTEXITCODE -ne 0) { throw 'Probe не собрался' }
& $csc /nologo /codepage:65001 /target:winexe "/out:$out\UiShot.exe" /r:Microsoft.CSharp.dll $app "$root\tests\UiShot.cs"
if ($LASTEXITCODE -ne 0) { throw 'UiShot не собрался' }

# --- приложение ---
$exe = "$root\AngardVPN.exe"
if (Test-Path $exe) {
    try { Remove-Item $exe -Force }
    catch {
        # Запущенный exe нельзя удалить, но можно переименовать: новая сборка встанет на его место.
        Get-ChildItem "$root\AngardVPN.old-*.exe" | Remove-Item -Force -ErrorAction SilentlyContinue
        Rename-Item $exe ("AngardVPN.old-{0:HHmmss}.exe" -f (Get-Date))
        'ВНИМАНИЕ: старая версия AngardVPN ещё запущена. Закройте её через значок у часов (Выход).'
    }
}
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu `
    "/out:$exe" "/win32manifest:$root\src\app.manifest" "/win32icon:$out\app.ico" `
    /r:Microsoft.CSharp.dll $app "$root\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'Приложение не собралось' }
"Готово: $exe ({0:N0} КБ)" -f ((Get-Item $exe).Length / 1KB)
