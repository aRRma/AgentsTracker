# Пробный экземпляр шлюза с темами для смоук-теста. Запуск из PowerShell в фоне:
#   pwsh -NoProfile -File tasks\topics\run-scratch.ps1
# Перед ним — сборка в отдельную папку (боевой шлюз не трогаем):
#   dotnet build <worktree>\src\AgentsTracker.Gateway -o $env:TEMP\at-build-topics
# Лог: $env:TEMP\at-topics-smoke\scratch.log, следить — Get-Content -Wait.
# Остановка — по пути exe, иначе ляжет и выпуск:
#   Get-Process AgentsTracker.Gateway | Where-Object { $_.Path -like "$env:TEMP*" } | Stop-Process -Force
$smoke = Join-Path $env:TEMP 'at-topics-smoke'
foreach ($name in 'alpha', 'beta') {
    $project = Join-Path $smoke $name
    if (-not (Test-Path $project)) {
        New-Item -ItemType Directory -Force $project | Out-Null
        git -C $project init -q
        "# $name" | Out-File -Encoding utf8 (Join-Path $project 'README.md')
    }
}

# Тестовый бот и AllowedUserIds — в appsettings.Local.json этой папки данных, не в репозитории.
$env:Gateway__DataDirectory = "$env:LOCALAPPDATA\AgentsTracker-scratch-topics"
$env:Gateway__McpPort = '5198'
$env:Gateway__MonitorPort = '5199'
$env:Gateway__ProjectsRoot = $smoke
$env:Gateway__ProjectPath = Join-Path $smoke 'alpha'
$env:Gateway__Model = 'haiku'
$env:Gateway__Question__Model = 'haiku'
& "$env:TEMP\at-build-topics\AgentsTracker.Gateway.exe" *>&1 | Tee-Object -FilePath (Join-Path $smoke 'scratch.log')
