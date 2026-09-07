<#
.SYNOPSIS
    Регистрирует шлюз в Планировщике задач с запуском при входе пользователя.

.DESCRIPTION
    Задача создаётся от текущего пользователя и без повышения прав: Claude Code читает
    OAuth-логин из %USERPROFILE%\.claude, и под другой учётной записью (например SYSTEM)
    он его не найдёт. Поэтому Windows-службой шлюз ставить нельзя.

    Секреты (токен бота, список пользователей) живут в %LOCALAPPDATA%\AgentsTracker\
    appsettings.Local.json, а не в папке публикации: publish\ можно пересобирать и удалять,
    не трогая конфиг. При первом запуске скрипт переносит туда appsettings.Local.json из
    папки проекта и шифрует секреты DPAPI (команда protect-secrets самого шлюза).

.PARAMETER InstallDir
    Куда публиковать и откуда запускать exe. По умолчанию publish\ в корне репозитория.
    Для промышленного запуска — папка вне репозитория, например C:\Apps\AgentsTracker.

.PARAMETER SelfContained
    Публиковать вместе со средой выполнения (win-x64): на машине не нужен .NET.

.PARAMETER SkipPublish
    Не собирать: в InstallDir уже лежит готовая публикация, скопированная с другой машины.
    Исходники и .NET SDK при этом не нужны — достаточно самого скрипта.

.EXAMPLE
    pwsh -File scripts\install-autostart.ps1
    pwsh -File scripts\install-autostart.ps1 -InstallDir C:\Apps\AgentsTracker -SelfContained
    pwsh -File install-autostart.ps1 -InstallDir C:\Apps\AgentsTracker -SkipPublish
    pwsh -File scripts\install-autostart.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$TaskName = 'AgentsTracker Gateway',
    [string]$InstallDir,
    [switch]$SelfContained,
    [switch]$SkipPublish,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Задача '$TaskName' удалена."
    return
}

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\AgentsTracker.Gateway'
$publish = if ($InstallDir) { [IO.Path]::GetFullPath($InstallDir) } else { Join-Path $root 'publish' }
$dataDir = Join-Path $env:LOCALAPPDATA 'AgentsTracker'

if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publish 'AgentsTracker.Gateway.exe'))) {
        throw "В $publish нет AgentsTracker.Gateway.exe — скопируйте туда папку публикации или уберите -SkipPublish."
    }
} else {
    if (-not (Test-Path $project)) { throw "Не найдены исходники: $project. Без них публикация невозможна — используйте -SkipPublish." }
    # Запущенный шлюз держит exe: publish упал бы с MSB3021, а задача осталась бы на старой версии.
    Get-Process AgentsTracker.Gateway -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($publish, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Write-Host "Останавливаю запущенный шлюз (PID $($_.Id))..."; Stop-Process -Id $_.Id -Force }

    Write-Host "Публикация в $publish..."
    $publishArgs = @($project, '-c', 'Release', '-o', $publish, '--nologo')
    if ($SelfContained) { $publishArgs += @('-r', 'win-x64', '--self-contained') }
    dotnet publish @publishArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }
}

$exe = Join-Path $publish 'AgentsTracker.Gateway.exe'
if (-not (Test-Path $exe)) { throw "После публикации не найден $exe" }

$dataLocal = Join-Path $dataDir 'appsettings.Local.json'
$besideExe = Join-Path $publish 'appsettings.Local.json'
# Откуда взять конфиг для первого запуска: из папки проекта (запуск из репозитория) или
# рядом с exe (готовую публикацию принесли на другую машину вместе с заполненным файлом).
$source = @((Join-Path $project 'appsettings.Local.json'), $besideExe) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (Test-Path $dataLocal) {
    & $exe protect-secrets | Out-Null
} elseif ($source) {
    # protect-secrets сам перенесёт файл в папку данных и зашифрует BotToken/Proxy.
    & $exe protect-secrets $source
    if ($LASTEXITCODE -ne 0) { throw 'protect-secrets завершился с ошибкой' }
} else {
    Write-Warning "Нет конфига: положите заполненный appsettings.Local.json в $dataDir и перезапустите задачу."
}

# Рядом с exe секретам не место: publish копирует всё из папки проекта, включая Local.json.
Remove-Item $besideExe -ErrorAction SilentlyContinue

# Папка данных — только владельцу и SYSTEM: там токен бота, секрет MCP и журнал аудита.
if (Test-Path $dataDir) {
    icacls $dataDir /inheritance:r /grant:r "${env:USERDOMAIN}\${env:USERNAME}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
}

$action   = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $publish
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Задача '$TaskName' зарегистрирована. Запустить сейчас: Start-ScheduledTask -TaskName '$TaskName'"
