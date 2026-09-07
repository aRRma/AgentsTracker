<#
.SYNOPSIS
    Переносит настройки канала в Gateway:Channel:Settings — разовая миграция конфига.

.DESCRIPTION
    BotToken, AllowedUserIds и Proxy канала переехали из корня секции Gateway в
    Gateway:Channel:Settings: их читает только модуль канала. Старый конфиг новый шлюз
    не запускает — он говорит, что ключи переехали, и просит перенести их.

    Скрипт правит appsettings.Local.json на месте, рядом кладёт .backup. Значения
    dpapi:… переносятся как есть: DPAPI привязан к учётной записи, а не к имени ключа,
    и повторно шифровать их не нужно.

    Gateway:Proxy остаётся общим прокси машины — через него ходит и агент. Если прокси
    нужен только каналу, перенесите его в Settings руками.

.PARAMETER Path
    Файл конфига. По умолчанию боевой: %LOCALAPPDATA%\AgentsTracker\appsettings.Local.json.
#>
[CmdletBinding()]
param(
    [string]$Path = "$env:LOCALAPPDATA\AgentsTracker\appsettings.Local.json"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) { throw "Не найден конфиг: $Path" }

$json = Get-Content $Path -Raw | ConvertFrom-Json
if (-not $json.Gateway) { throw "В $Path нет секции Gateway." }

$gateway = $json.Gateway
$moved = @()

if (-not $gateway.Channel) {
    $gateway | Add-Member -NotePropertyName Channel -NotePropertyValue ([pscustomobject]@{ Type = 'telegram' }) -Force
}
if (-not $gateway.Channel.Settings) {
    $gateway.Channel | Add-Member -NotePropertyName Settings -NotePropertyValue ([pscustomobject]@{}) -Force
}

foreach ($key in 'BotToken', 'AllowedUserIds') {
    if ($null -eq $gateway.$key) { continue }

    $gateway.Channel.Settings | Add-Member -NotePropertyName $key -NotePropertyValue $gateway.$key -Force
    $gateway.PSObject.Properties.Remove($key)
    $moved += $key
}

if ($moved.Count -eq 0) {
    Write-Output "Переносить нечего: BotToken и AllowedUserIds уже не в корне секции Gateway."
    exit 0
}

$backup = "$Path.backup"
Copy-Item $Path $backup -Force

# Кириллица в комментариях «//Ключ» должна остаться читаемой, поэтому пишем UTF-8 без BOM
# и без escape-последовательностей ConvertTo-Json (он их не добавляет для не-ASCII).
$text = $json | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($Path, $text, (New-Object Text.UTF8Encoding $false))

Write-Output "Перенесено в Gateway:Channel:Settings: $($moved -join ', ')"
Write-Output "Копия старого конфига: $backup"
Write-Output "Проверьте файл и запустите шлюз."
