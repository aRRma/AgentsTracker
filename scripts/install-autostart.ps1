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

.EXAMPLE
    pwsh -File scripts\install-autostart.ps1
    pwsh -File scripts\install-autostart.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$TaskName = 'AgentsTracker Gateway',
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
$publish = Join-Path $root 'publish'
$dataDir = Join-Path $env:LOCALAPPDATA 'AgentsTracker'

Write-Host 'Публикация...'
dotnet publish $project -c Release -o $publish --nologo | Out-Null

$exe = Join-Path $publish 'AgentsTracker.Gateway.exe'
if (-not (Test-Path $exe)) { throw "После публикации не найден $exe" }

# В publish\ секретам не место: publish копирует всё из папки проекта, включая Local.json.
Remove-Item (Join-Path $publish 'appsettings.Local.json') -ErrorAction SilentlyContinue

$dataLocal = Join-Path $dataDir 'appsettings.Local.json'
$projectLocal = Join-Path $project 'appsettings.Local.json'

if (-not (Test-Path $dataLocal) -and (Test-Path $projectLocal)) {
    # protect-secrets сам перенесёт файл в папку данных и зашифрует секреты канала и Proxy.
    & $exe protect-secrets $projectLocal
    if ($LASTEXITCODE -ne 0) { throw 'protect-secrets завершился с ошибкой' }
} elseif (Test-Path $dataLocal) {
    & $exe protect-secrets | Out-Null
} else {
    Write-Warning "Нет ни $dataLocal, ни $projectLocal — заполните конфиг перед первым запуском."
}

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
