# Сборка релиза Cs2LatencyDoctor.
#
# Собирает три варианта, как их ждут на странице загрузки:
#
#   full     — всё внутри, .NET не нужен. Для большинства людей.
#   lite     — маленький архив, нужен установленный .NET 10 Desktop Runtime.
#   console  — версия без окна, для тех, кто любит командную строку.
#
# В каждый пакет кладутся:
#   ПРОЧТИ-МЕНЯ.md  — описание программы с актуальным номером версии
#   ЛИЦЕНЗИЯ.txt    — текст лицензии
#   ЗАПУСТИТЬ.bat   — ярлык запуска для вариантов с окном
#
# Готовые архивы выкладываются в раздел Releases на GitHub. В сам репозиторий
# собранное не попадает: там только исходники.

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$release = Join-Path $root 'release'

# Номер версии берём из проекта, чтобы не расходился с программой.
$coreProject = Join-Path $src 'Cs2LatencyDoctor.Core\Cs2LatencyDoctor.Core.csproj'
$coreXml = [xml](Get-Content $coreProject -Encoding UTF8)
$version = $coreXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (-not $version) {
    Write-Host '  Не удалось прочитать версию из проекта' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host ('  Сборка Cs2LatencyDoctor ' + $version) -ForegroundColor Cyan
Write-Host '  ================================================================'
Write-Host ''

# --- чистим прошлые сборки этой версии -------------------------------------

Get-ChildItem $release -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like ('Cs2LatencyDoctor-' + $version + '-*') } |
    ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force
        Write-Host ('  Убрана старая папка: ' + $_.Name)
    }

Get-ChildItem $release -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like ('Cs2LatencyDoctor-' + $version + '-*.zip') } |
    ForEach-Object { Remove-Item $_.FullName -Force }

# --- описание программы с актуальной версией -------------------------------

function New-Readme {
    param([string]$Variant, [string]$SizeText)

    $whatToDo = switch ($Variant) {
        'full'    { 'Распакуйте архив и запустите **`ЗАПУСТИТЬ.bat`**. Установка не требуется.' }
        'lite'    { 'Распакуйте архив и запустите **`ЗАПУСТИТЬ.bat`**. Понадобится установленный .NET 10 Desktop Runtime — программа скажет, где взять, если его нет.' }
        'console' { 'Распакуйте архив и запустите **`cs2latency.exe`** в командной строке. Версия без окна: удобна для скриптов и удалённых проверок.' }
    }

    $extra = switch ($Variant) {
        'console' {
            "`n## Как пользоваться консольной версией`n`n" +
            "``````" + "`n" +
            "cs2latency.exe                 проверка и отчёт в окне консоли`n" +
            "cs2latency.exe --history       что изменилось с прошлых замеров`n" +
            "cs2latency.exe --help          все возможности`n" +
            "``````" + "`n`n" +
            "Отчёт сохраняется в папку ``отчёты`` рядом с программой.`n"
        }
        default {
            "`n## Как пользоваться`n`n" +
            "1. Распакуйте архив в любую папку.`n" +
            "2. Запустите ``ЗАПУСТИТЬ.bat``.`n" +
            "3. Нажмите **«Проверить компьютер»** и дождитесь окончания — это около минуты.`n" +
            "4. Посмотрите находки. Программа объяснит каждую: что нашли, почему это влияет`n" +
            "   на игру и что с этим делать.`n" +
            "5. Если хотите применить исправления — нужны права администратора.`n" +
            "   Каждое изменение можно вернуть кнопкой **«Откатить изменения»**.`n`n" +
            "**Программа сама ничего не меняет.** Проверка работает без прав и ничего не трогает.`n"
        }
    }

    # Собираем текст по частям. Раньше здесь была одна строка со вставками,
    # и в описание попали имена переменных вместо значений: получалось
    # «архив ' + $Variant + ', версия ' + $version + '». Поэтому теперь
    # части складываются по отдельности и проверяются.
    $head = "# Cs2LatencyDoctor`n`n"
    $head += "[![Скачать](https://img.shields.io/github/v/release/MVitalii177A/Cs2LatencyDoctor?label=скачать&color=2EA043)](https://github.com/MVitalii177A/Cs2LatencyDoctor/releases/latest)`n"
    $head += "[![Лицензия](https://img.shields.io/badge/лицензия-MIT-blue)](https://github.com/MVitalii177A/Cs2LatencyDoctor/blob/main/LICENSE)`n`n"
    $head += "Бесплатный открытый инструмент для Windows: находит причины «пули не регистрируются»,`n"
    $head += "инпутлага и дёрганости в Counter-Strike 2 — и показывает **доказательства замерами**,`n"
    $head += "а не «оптимизируй всё».`n`n"
    $head += "**Программа бесплатная.** Никаких платных версий и ограничений — всё, что она умеет,`n"
    $head += "доступно всем. Если она вам помогла и захочется отблагодарить — в программе есть кнопка`n"
    $head += "**«♥ Поблагодарить»** со ссылками. Это по желанию и ни на что не влияет.`n`n"
    $head += "**Это архив " + $Variant + ", версия " + $version + "** (" + $SizeText + ").`n`n"

    $table = "## Как получить`n`n"
    $table += "Готовые файлы — в разделе [**Releases**](https://github.com/MVitalii177A/Cs2LatencyDoctor/releases/latest).`n`n"
    $table += "| Файл | Нужен ли .NET |`n"
    $table += "|---|---|`n"
    $table += "| ``Cs2LatencyDoctor-" + $version + "-full.zip`` | **нет** — всё внутри |`n"
    $table += "| ``Cs2LatencyDoctor-" + $version + "-lite.zip`` | да, .NET 10 Desktop Runtime |`n"
    $table += "| ``Cs2LatencyDoctor-" + $version + "-console.zip`` | **нет** — версия без окна |`n`n"
    $table += "Не знаете, что выбрать — берите ``full``.`n`n"

    $task = "## Что делать с этим архивом`n`n" + $whatToDo + "`n"

    $tail = "`n## Зачем это нужно`n`n"
    $tail += "Большинство «оптимизаторов для CS2» применяют список твиков и просят поверить на слово.`n"
    $tail += "Этот инструмент делает наоборот: сначала измеряет, потом чинит, потом измеряет снова.`n`n"
    $tail += "Что он умеет:`n`n"
    $tail += "- **измеряет задержку** до роутера и до интернета, считает разброс и всплески;`n"
    $tail += "- **проверяет настройки CS2** — режим экрана, вертикальную синхронизацию, лимит кадров, Reflex;`n"
    $tail += "- **находит причины** в самой Windows: питание, планировщик, оверлеи, сетевую карту;`n"
    $tail += "- **объясняет каждую находку** человеческим языком: что нашли, почему важно, что делать;`n"
    $tail += "- **честно говорит, когда помочь не может** — и почему, и что делать самому;`n"
    $tail += "- **предупреждает о системных правках**: часть исправлений меняет настройки Windows,`n"
    $tail += "  поэтому затронет не только CS2, но и другие игры — об этом сказано до применения;`n"
    $tail += "- **возвращает всё назад** по кнопке: каждое изменение записывается в журнал.`n`n"
    $tail += "## Чего программа НЕ делает`n`n"
    $tail += "- Не применяет «оптимизации» без спроса: проверка ничего не меняет.`n"
    $tail += "- Не отправляет данные наружу. Ни статистики, ни отчётов — ничего.`n"
    $tail += "- Не ставит ничего в автозапуск без вашего ведома.`n"
    $tail += "- Не обещает чудес: если проблема у провайдера, программа так и скажет.`n`n"
    $tail += "## Требования`n`n"
    $tail += "Windows 10 или 11, 64-разрядная. Для варианта ``lite`` нужен`n"
    $tail += "[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).`n`n"
    $tail += "## Исходники и сообщения о проблемах`n`n"
    $tail += "Исходный код: <https://github.com/MVitalii177A/Cs2LatencyDoctor>`n`n"
    $tail += "Нашли ошибку или что-то непонятно — напишите в`n"
    $tail += "[Issues](https://github.com/MVitalii177A/Cs2LatencyDoctor/issues/new).`n"
    $tail += "В программе есть кнопка **«Сообщить о проблеме»**: она соберёт отчёт о системе,`n"
    $tail += "который помогает разобраться. Имени компьютера и ваших личных данных в нём нет.`n`n"
    $tail += "## Лицензия`n`n"
    $tail += "MIT — можно свободно использовать, изучать и изменять. Текст в файле ``ЛИЦЕНЗИЯ.txt``.`n"

    $text = $head + $table + $task + $extra + $tail

    # Проверка: в текст не должны попасть имена переменных вместо значений.
    # Именно эта ошибка была в прошлой сборке, и её лучше ловить сразу.
    foreach ($bad in @('$Variant', '$version', '$SizeText', '$whatToDo')) {
        if ($text.Contains($bad)) {
            throw ('В описание попал текст переменной вместо значения: ' + $bad)
        }
    }

    return $text
}
function Write-PackageFiles {
    param([string]$Folder, [string]$Variant)

    # Описание программы
    $size = Get-ChildItem $Folder -Recurse -File |
        Measure-Object -Property Length -Sum
    $sizeText = [math]::Round($size.Sum / 1MB, 1).ToString() + ' МБ'

    $readme = New-Readme -Variant $Variant -SizeText $sizeText
    [System.IO.File]::WriteAllText((Join-Path $Folder 'ПРОЧТИ-МЕНЯ.md'), $readme,
        (New-Object System.Text.UTF8Encoding($false)))

    # Лицензия
    $license = Join-Path $root 'LICENSE'
    if (Test-Path $license) {
        Copy-Item $license (Join-Path $Folder 'ЛИЦЕНЗИЯ.txt') -Force
    }
}

function Write-Launcher {
    param([string]$Folder, [string]$ExeName, [string]$Variant)

    $lines = @()
    $lines += '@echo off'
    $lines += 'chcp 65001 >nul'
    $lines += 'title Cs2LatencyDoctor ' + $version
    $lines += 'echo.'
    $lines += 'echo   Запускаю Cs2LatencyDoctor ' + $version + '...'
    $lines += 'echo.'
    $lines += 'echo   Программа проверит компьютер и покажет, что мешает играть в CS2.'
    $lines += 'echo   Сама она ничего не меняет: исправления применяются только по кнопке,'
    $lines += 'echo   и каждое изменение можно вернуть обратно.'
    $lines += 'echo.'
    $lines += 'echo   Для применения исправлений нужны права администратора.'
    $lines += 'echo.'
    $lines += 'start "" "%~dp0' + $ExeName + '"'

    [System.IO.File]::WriteAllText((Join-Path $Folder 'ЗАПУСТИТЬ.bat'),
        ($lines -join "`r`n"), [System.Text.Encoding]::GetEncoding(866))
}

function New-Zip {
    param([string]$Folder)

    $zip = $Folder + '.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }

    # Именно ZipFile, а не Compress-Archive: тот пропускает вложенные папки,
    # и в прошлый раз архив получался неполным.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $Folder, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

    return $zip
}

# --- 1. full: всё внутри, .NET не нужен ------------------------------------

Write-Host '  [1/3] Собираю full — со всем .NET внутри...' -ForegroundColor Cyan

$full = Join-Path $release ('Cs2LatencyDoctor-' + $version + '-full')

& dotnet publish (Join-Path $src 'Cs2LatencyDoctor.Gui\Cs2LatencyDoctor.Gui.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none `
    -o $full --nologo 2>&1 | Select-String 'error|Ошибок' | ForEach-Object { '     ' + $_.Line.Trim() }

Write-PackageFiles -Folder $full -Variant 'full'
Write-Launcher -Folder $full -ExeName 'Cs2LatencyDoctor.Gui.exe' -Variant 'full'

$fullSize = [math]::Round((Get-ChildItem $full -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host ('     файлов: ' + @(Get-ChildItem $full -File).Count + ', размер: ' + $fullSize + ' МБ') -ForegroundColor Green

# --- 2. lite: нужен установленный .NET -------------------------------------

Write-Host ''
Write-Host '  [2/3] Собираю lite — без .NET, нужен установленный...' -ForegroundColor Cyan

$lite = Join-Path $release ('Cs2LatencyDoctor-' + $version + '-lite')

& dotnet publish (Join-Path $src 'Cs2LatencyDoctor.Gui\Cs2LatencyDoctor.Gui.csproj') `
    -c Release --self-contained false `
    -p:DebugType=none `
    -o $lite --nologo 2>&1 | Select-String 'error|Ошибок' | ForEach-Object { '     ' + $_.Line.Trim() }

Write-PackageFiles -Folder $lite -Variant 'lite'
Write-Launcher -Folder $lite -ExeName 'Cs2LatencyDoctor.Gui.exe' -Variant 'lite'

$liteSize = [math]::Round((Get-ChildItem $lite -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 2)
Write-Host ('     файлов: ' + @(Get-ChildItem $lite -File).Count + ', размер: ' + $liteSize + ' МБ') -ForegroundColor Green

# --- 3. console: без окна --------------------------------------------------

Write-Host ''
Write-Host '  [3/3] Собираю console — версия без окна...' -ForegroundColor Cyan

$console = Join-Path $release ('Cs2LatencyDoctor-' + $version + '-console')

& dotnet publish (Join-Path $src 'Cs2LatencyDoctor.Cli\Cs2LatencyDoctor.Cli.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none `
    -o $console --nologo 2>&1 | Select-String 'error|Ошибок' | ForEach-Object { '     ' + $_.Line.Trim() }

Write-PackageFiles -Folder $console -Variant 'console'

$consoleSize = [math]::Round((Get-ChildItem $console -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host ('     файлов: ' + @(Get-ChildItem $console -File).Count + ', размер: ' + $consoleSize + ' МБ') -ForegroundColor Green

# --- архивы ----------------------------------------------------------------

Write-Host ''
Write-Host '  Собираю архивы...' -ForegroundColor Cyan

$zips = @()
foreach ($folder in @($full, $lite, $console)) {
    $zip = New-Zip -Folder $folder
    $zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    $zips += [pscustomobject]@{ Путь = $zip; МБ = $zipSize }
    Write-Host ('     ' + (Split-Path $zip -Leaf) + '  —  ' + $zipSize + ' МБ') -ForegroundColor Green
}

# --- проверка, что архивы целые --------------------------------------------

Write-Host ''
Write-Host '  Проверяю архивы...' -ForegroundColor Cyan

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($zip in $zips) {
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip.Путь)
        $count = $archive.Entries.Count
        $hasExe = @($archive.Entries | Where-Object { $_.Name -like '*.exe' }).Count
        $hasReadme = @($archive.Entries | Where-Object { $_.Name -eq 'ПРОЧТИ-МЕНЯ.md' }).Count
        $hasLicense = @($archive.Entries | Where-Object { $_.Name -eq 'ЛИЦЕНЗИЯ.txt' }).Count
        $archive.Dispose()

        $ok = ($count -gt 5) -and ($hasExe -gt 0) -and ($hasReadme -gt 0) -and ($hasLicense -gt 0)
        $mark = if ($ok) { 'OK' } else { 'ПРОБЛЕМА' }

        Write-Host ('     ' + (Split-Path $zip.Путь -Leaf).PadRight(44) +
                    'файлов ' + $count.ToString().PadLeft(4) +
                    ', exe ' + $hasExe +
                    ', описание ' + $hasReadme +
                    ', лицензия ' + $hasLicense + '   ' + $mark)
    }
    catch {
        Write-Host ('     ' + (Split-Path $zip.Путь -Leaf) + ' — ОШИБКА: ' + $_.Exception.Message) -ForegroundColor Red
    }
}

# --- итог ------------------------------------------------------------------

Write-Host ''
Write-Host '  ================================================================' -ForegroundColor Cyan
Write-Host ('  ГОТОВО. Версия ' + $version) -ForegroundColor Green
Write-Host ''
Write-Host '  Что дальше:'
Write-Host '     Архивы лежат в папке release. Осталось выложить их в раздел'
Write-Host '     Releases на GitHub вместе с описанием изменений.'
Write-Host ''
