<#
.SYNOPSIS
    Регистрирует шлюз в Планировщике задач с запуском при входе пользователя.

.DESCRIPTION
    Задача создаётся от текущего пользователя и без повышения прав: Claude Code читает
    OAuth-логин из %USERPROFILE%\.claude, и под другой учётной записью (например SYSTEM)
    он его не найдёт. Поэтому Windows-службой шлюз ставить нельзя.

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

Write-Host 'Публикация...'
dotnet publish $project -c Release -o $publish --nologo | Out-Null

$exe = Join-Path $publish 'AgentsTracker.Gateway.exe'
if (-not (Test-Path $exe)) { throw "После публикации не найден $exe" }

$local = Join-Path $project 'appsettings.Local.json'
if (Test-Path $local) {
    Copy-Item $local (Join-Path $publish 'appsettings.Local.json') -Force
} else {
    Write-Warning "Нет $local — заполните appsettings.Local.json перед первым запуском."
}

$action   = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $publish
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Задача '$TaskName' зарегистрирована. Запустить сейчас: Start-ScheduledTask -TaskName '$TaskName'"
