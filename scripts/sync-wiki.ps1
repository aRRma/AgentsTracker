<#
.SYNOPSIS
Собирает вики GitHub из README и русских docs/*.md и отправляет её в репозиторий вики.

.DESCRIPTION
Единственный источник документации — репозиторий; вики только повторяет его, поэтому
страницы каждый раз генерируются заново, а не правятся руками. Ссылки на файлы репозитория
и картинки переписываются в абсолютные: вики лежит в отдельном git-репозитории и
относительных путей к основному не видит.

Вики читателю-человеку, поэтому берутся только русские docs/*.md; английское зеркало
docs/en/ для агента сюда не попадает.

.PARAMETER OutDir
Собрать страницы в указанную папку и ничего не отправлять — посмотреть, что получится.

.PARAMETER NoPush
Подготовить клон вики с изменениями, но не пушить.

.EXAMPLE
pwsh -File scripts\sync-wiki.ps1 -OutDir C:\Temp\wiki-preview
.EXAMPLE
pwsh -File scripts\sync-wiki.ps1
#>
[CmdletBinding()]
param(
    [string] $Repository = 'aRRma/AgentsTracker',
    [string] $Branch = 'master',

    # Готовый клон вики. Пусто — склонировать самому.
    [string] $WikiPath,

    [string] $OutDir,
    [switch] $NoPush
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$raw = "https://raw.githubusercontent.com/$Repository/$Branch"
$blob = "https://github.com/$Repository/blob/$Branch"
$tree = "https://github.com/$Repository/tree/$Branch"

# Какая страница получается из какого файла. Имена латиницей: они попадают в адрес страницы.
$pages = [ordered]@{
    'use-cases'          = 'use-cases'
    'safety'             = 'safety'
    'deployment'         = 'deployment'
    'operations'         = 'operations'
    'architecture'       = 'architecture'
    'extending'          = 'extending'
    'glossary'           = 'glossary'
    'monitor'            = 'monitor'
    'cli-contract'       = 'cli-contract'
    'claude-permissions' = 'claude-permissions'
}

function Convert-Links {
    param([string] $Text)

    # Ссылки на страницы вики: [текст](docs/monitor.md) → [текст](monitor).
    foreach ($doc in $pages.Keys) {
        $Text = $Text -replace "\]\((?:docs/)?$doc\.md(#[^)]*)?\)", "]($($pages[$doc])`$1)"
    }

    # Картинки лежат в репозитории: вики их по относительному пути не найдёт.
    $Text = $Text -replace 'src="(docs/[^"]+)"', "src=`"$raw/`$1`""
    $Text = $Text -replace '\]\((docs/images/[^)]+)\)', "]($raw/`$1)"

    # Остальные файлы и папки репозитория — на страницу GitHub, а не в вики.
    $Text = $Text -replace '\]\(docs/\)', "]($tree/docs)"
    $Text = $Text -replace '\]\(docs/examples/([^)]+)\)', "]($blob/docs/examples/`$1)"
    $Text = $Text -replace '\]\((CLAUDE\.md|README\.md|Dockerfile|scripts/[^)]+)\)', "]($blob/`$1)"

    return $Text
}

function Get-Title {
    param([string] $Path)

    # Заголовок страницы для боковой панели берём из первого «# » файла.
    $line = Select-String -Path $Path -Pattern '^# (.+)$' | Select-Object -First 1
    if ($line) { return $line.Matches[0].Groups[1].Value }
    return [IO.Path]::GetFileNameWithoutExtension($Path)
}

function Write-Page {
    param([string] $Path, [string] $Text)
    # Без BOM: вики отдаёт файлы как есть, лишние байты видны в заголовке страницы.
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

# --- куда собираем ---------------------------------------------------------

# Путь уходит и в [IO.File] (считает относительный от каталога процесса), и в git -C (от
# каталога PowerShell): разойдясь, они разложат страницы мимо клона, и синхронизация молча
# закончится словами «коммитить нечего». Поэтому приводим к абсолютному сразу.
$cloned = $false
if ($OutDir) {
    $target = [IO.Path]::GetFullPath($OutDir, $PWD.Path)
    # Папку предпросмотра называет человек, а скрипт удаляет в ней страницы: -OutDir docs
    # или -OutDir . стёр бы исходную документацию.
    if ($target -eq $root -or $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Папка предпросмотра лежит внутри репозитория ($target). Укажите -OutDir вне него."
    }
    New-Item -ItemType Directory $target -Force | Out-Null
}
elseif ($WikiPath) {
    $target = [IO.Path]::GetFullPath($WikiPath, $PWD.Path)
}
else {
    $target = Join-Path ([IO.Path]::GetTempPath()) "AgentsTracker.wiki-$(Get-Random)"
    Write-Host "Клонирую вики в $target"
    git clone "https://github.com/$Repository.wiki.git" $target
    if ($LASTEXITCODE -ne 0) {
        throw "Вики недоступна. Если она ещё не создана, откройте https://github.com/$Repository/wiki и сохраните первую страницу — GitHub заводит репозиторий вики только после этого."
    }
    $cloned = $true
}

$names = @('Home.md', '_Sidebar.md', '_Footer.md') + @($pages.Values | ForEach-Object { "$_.md" })

if ($OutDir) {
    # Папка предпросмотра чужая: чистим только свои страницы, соседние .md не наши.
    foreach ($name in $names) { Remove-Item (Join-Path $target $name) -ErrorAction Ignore }
}
else {
    # Клон вики пересобираем целиком: правки прямо в вики не переживут синхронизацию, а
    # страница, исчезнувшая из docs/, должна исчезнуть и здесь.
    Get-ChildItem $target -Filter *.md -File | Remove-Item
}

# --- страницы --------------------------------------------------------------

Write-Page (Join-Path $target 'Home.md') (Convert-Links (Get-Content (Join-Path $root 'README.md') -Raw))

$sidebar = [Text.StringBuilder]::new()
[void]$sidebar.AppendLine('### AgentsTracker')
[void]$sidebar.AppendLine()
[void]$sidebar.AppendLine('* [Обзор проекта](Home)')

foreach ($doc in $pages.Keys) {
    $source = Join-Path $root "docs/$doc.md"
    Write-Page (Join-Path $target "$($pages[$doc]).md") (Convert-Links (Get-Content $source -Raw))
    [void]$sidebar.AppendLine("* [$(Get-Title $source)]($($pages[$doc]))")
}

[void]$sidebar.AppendLine()
[void]$sidebar.AppendLine('---')
[void]$sidebar.AppendLine()
[void]$sidebar.AppendLine("[Скачать сборку](https://github.com/$Repository/releases)")
Write-Page (Join-Path $target '_Sidebar.md') $sidebar.ToString()

$footer = @"
_Страницы собираются из ``README.md`` и ``docs/`` в [репозитории]($tree) — правки прямо здесь
затрёт следующая синхронизация._
"@
Write-Page (Join-Path $target '_Footer.md') $footer

Write-Host "Собрано страниц: $((Get-ChildItem $target -Filter *.md -File).Count)"

if ($OutDir) {
    Write-Host "Готово: $target (только предпросмотр, ничего не отправлено)"
    return
}

# --- отправка --------------------------------------------------------------

git -C $target add -A
if (-not (git -C $target status --porcelain)) {
    Write-Host 'Вики уже совпадает с репозиторием, коммитить нечего.'
    if ($cloned) { Remove-Item $target -Recurse -Force }
    return
}

git -C $target commit -q -m "Синхронизация с репозиторием"
# Без проверки отказ коммита (нет user.email, подпись, hook) утёк бы в пустой пуш, и скрипт
# отрапортовал бы «Вики обновлена», ничего не отправив.
if ($LASTEXITCODE -ne 0) { throw "Не удалось закоммитить страницы (клон остался в $target)" }

if ($NoPush) {
    Write-Host "Коммит готов, пуш пропущен: $target"
    return
}

git -C $target push
if ($LASTEXITCODE -ne 0) {
    # Между клоном и пушем в вики успели записать (соседний запуск workflow или правка руками).
    # Наш набор страниц полный, поэтому просто подкладываемся под чужой коммит и пушим ещё раз.
    Write-Host 'Пуш отклонён, повторяю после rebase.'
    $wikiBranch = git -C $target rev-parse --abbrev-ref HEAD
    git -C $target pull --rebase origin $wikiBranch
    if ($LASTEXITCODE -ne 0) { throw "Вики изменилась, rebase не прошёл (клон остался в $target)" }

    git -C $target push
    if ($LASTEXITCODE -ne 0) { throw "Не удалось отправить вики (клон остался в $target)" }
}

Write-Host "Вики обновлена: https://github.com/$Repository/wiki"
if ($cloned) { Remove-Item $target -Recurse -Force }
