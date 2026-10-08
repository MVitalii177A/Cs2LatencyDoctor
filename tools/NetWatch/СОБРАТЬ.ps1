# Сборка NetWatch для выпуска

# Зачем отдельный файл. В проекте настройки автономной сборки стоят под условием,
# потому что иначе на NetWatch нельзя ссылаться из проверок. Этот скрипт
# включает условие и собирает один файл, готовый к запуску на любом компьютере.

param(
    [string]$Output = "$PSScriptRoot\выпуск"
)

$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host '  Сборка NetWatch' -ForegroundColor Cyan
Write-Host '  ──────────────────────────────────────────────'

if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
    Write-Host '  Прежняя сборка удалена'
}

dotnet publish "$PSScriptRoot\NetWatch.csproj" `
    -c Release `
    -p:NetWatchRelease=true `
    -o $Output `
    --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '  СБОРКА НЕ УДАЛАСЬ' -ForegroundColor Red
    exit 1
}

# Описание кладём рядом: человек должен понимать, что запускает.
$readme = Join-Path $PSScriptRoot 'ЧТО-ЭТО.md'

if (Test-Path $readme) {
    Copy-Item $readme (Join-Path $Output 'ЧТО-ЭТО.md') -Force
}

$exe = Join-Path $Output 'netwatch.exe'

Write-Host ''
Write-Host '  Готово:' -ForegroundColor Green

if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "     $exe  ($size МБ)"
    Write-Host '     Работает без установки .NET: файл можно скопировать куда угодно.'
}
else {
    Write-Host '     Файл netwatch.exe не найден — что-то пошло не так' -ForegroundColor Red
    exit 1
}

Write-Host ''
