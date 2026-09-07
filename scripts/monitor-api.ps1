<#
.SYNOPSIS
    Читает эндпоинт веб-монитора шлюза на 127.0.0.1 и печатает ответ.

.DESCRIPTION
    Единственный способ для агента посмотреть /api/* из сессии Claude Code: curl,
    Invoke-WebRequest и Invoke-RestMethod у него в deny для любых адресов, включая loopback
    (docs/claude-permissions.md), а этот скрипт разрешён в .claude/settings.json проекта.
    Адрес зашит: только http://127.0.0.1 и только один эндпоинт api/<имя> со строкой запроса.
    Ничего не меняет — монитор и сам умеет только читать.

.PARAMETER Path
    Путь эндпоинта: api/snapshot, api/runs?limit=5, api/log?count=50, api/stats.csv…
    Без ведущего слэша: Git Bash превращает аргумент «/api/…» в «C:/Program Files/Git/api/…»
    (подстановка путей MSYS), а «api/…» оставляет как есть. Со слэшем тоже принимается.

.PARAMETER Port
    Gateway:MonitorPort, по умолчанию 5100.

.EXAMPLE
    pwsh -File scripts\monitor-api.ps1 api/snapshot
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    # один сегмент после api/ (имя, при нём расширение) и строка запроса: «..» и вложенные
    # пути не проходят, иначе Uri нормализовал бы api/../x в /x за пределами /api/
    [ValidatePattern('^/?(api/[A-Za-z0-9_-]+(\.[A-Za-z0-9]+)?(\?[A-Za-z0-9_&=%.-]*)?)?$')]
    [string]$Path,

    [ValidateRange(1, 65535)]
    [int]$Port = 5100
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$uri = "http://127.0.0.1:$Port/$($Path.TrimStart('/'))"

try {
    $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 5
}
catch {
    Write-Error "Монитор не ответил на $uri — шлюз не запущен или MonitorPort другой: $($_.Exception.Message)"
    exit 1
}

# Content уже строка: JSON или CSV печатаем как есть, без разбора — читателю нужен сырой ответ.
$response.Content
