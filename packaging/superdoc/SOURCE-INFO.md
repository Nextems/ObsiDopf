# ObsiDopf · исходники SuperDoc 1.47.2

Собственный код ObsiDopf: Copyright (c) 2026 Maxim_Dembitsky (Nextems), AGPL-3.0-only.
SuperDoc: Harbour Enterprises, Inc., AGPL-3.0. Авторство и лицензии зависимостей сохраняются.
Программа предоставляется БЕЗ ГАРАНТИЙ; полный текст условий находится в LICENSE.

Исходники ObsiDopf: https://github.com/Nextems/ObsiDopf
Архивы опубликованных версий: https://github.com/Nextems/ObsiDopf/releases
Исходники используемого SuperDoc:
https://github.com/superdoc/docx-editor/tree/66c22186c7c0735360ca4fd94f7499898415e903

Для релиза 1.1.0 вместе с установщиком и исходниками ObsiDopf следует публиковать
ObsiDopf-1.1.0-SuperDoc-1.47.2-Source.zip. Этот документ описывает его содержимое.

## Содержимое архива

- upstream/superdoc-66c22186.zip — полный снимок репозитория SuperDoc, включая
  TypeScript/Vue/JavaScript, workspace-пакеты, pnpm-lock.yaml, конфигурацию и скрипты сборки.
- upstream/superdoc-1.47.2.tgz — точный исходный npm-пакет, использованный сборкой ObsiDopf.
- published-sources/ — все 2386 записей sourcesContent из карты опубликованного браузерного
  bundle, включая сторонние зависимости и виртуальный модуль Vite. index.json сохраняет
  исходные имена и хеши. Это дополнение к полному репозиторию, а не его замена.
- modified-source/packages/.../PresentationEditor.ts — исходник с изменениями ObsiDopf.
- obsidopf-integration/ — оболочка редактора, CSS, помощники кеша/выделения, оба способа
  применения изменений, зафиксированные npm-зависимости и скрипт подготовки ресурсов.
- source-lock.json — версия пакета, точный коммит и контрольные суммы исходных архивов.

Пакет npm не содержит gitHead и не имеет совпадающего публичного тега. Снимок выбран
по карте исходников опубликованного bundle: содержимое всех 1734 реальных файлов
репозитория совпало после нормализации переводов строк; отличающихся файлов нет.
Единственная дополнительная запись с путём packages/superdoc — виртуальный модуль Vite.
Полная карта содержит 2386 записей, без пропущенного sourcesContent.

## Повторная сборка штатного приложения

В архиве исходников ObsiDopf доступны .NET/Rust/NSIS-скрипты и инструкции основного README.
Для подготовки Word-ресурсов нужны Node.js 22+ и npm. В web/word выполнить:

    npm ci --no-audit --no-fund
    npm run build

Это использует зафиксированный superdoc@1.47.2 из npm и применяет patch-engine.mjs
к опубликованному браузерному bundle. Точный npm-пакет также приложен в upstream/;
его можно установить через npm install --no-save --package-lock=false <путь-к-tgz>.
После этого основной проект собирается обычным dotnet build/build.ps1.

## Изменение и сборка самого движка из TypeScript/Vue

1. Распаковать upstream/superdoc-66c22186.zip в отдельную папку. В корне снимка есть
   package.json, pnpm-workspace.yaml, pnpm-lock.yaml и исходники всех workspace-пакетов.
2. Для внесения исходных изменений ObsiDopf выполнить:

       node "<путь-к-ObsiDopf>/web/word/patch-engine-source.mjs" "<корень-снимка-SuperDoc>"

   Либо скопировать PresentationEditor.ts из modified-source/ поверх одноимённого файла
   в снимке. Применять нужно один способ, один раз. Для дальнейших собственных изменений
   редактировать TypeScript/Vue после применения этого исходного патча.
3. Использовать Node.js 22+, Corepack и pnpm 10.25.0 (версия указана в packageManager
   снимка). Для сборки самого движка ниже используется Bash в Linux/WSL2:
   upstream prepare содержит shell-команду с синтаксисом Bash. В корне снимка выполнить:

       export CI=1
       corepack pnpm install --frozen-lockfile
       node -e 'const fs=require("node:fs"); const f="packages/superdoc/package.json"; const p=JSON.parse(fs.readFileSync(f,"utf8")); p.version="1.47.2"; fs.writeFileSync(f,JSON.stringify(p,null,2)+"\n");'
       corepack pnpm --dir packages/superdoc exec vite build --config vite.config.cdn.js

   CI=1 отключает установку git hooks. Здесь нет команд запуска тестов. Команда Node
   задаёт обозначение версии 1.47.2 перед сборкой: в исходном снимке поле версии —
   1.46.2, опубликованный npm-пакет имеет версию 1.47.2. При работе в WSL2 снимок можно
   распаковать на диске Windows и открыть его через /mnt/c, чтобы получить dist
   для следующего шага в основной Windows-сборке.
4. Подключить полученные dist/superdoc.min.js и style.css в ObsiDopf:

       node "<путь-к-ObsiDopf>/web/word/build.mjs" --engine-dist "<корень-снимка-SuperDoc>/packages/superdoc/dist"

   Этот явный режим принимает bundle с уже внесёнными исходными изменениями; повторный
   патч минифицированного файла не применяется. Затем собрать .NET-проект. Обычный
   npm run build без --engine-dist возвращает штатный зафиксированный npm-bundle.

Сборка TypeScript/Vue из снимка в рамках подготовки архива не запускалась. Побайтовое
совпадение результата Vite с npm-bundle не заявляется; полные исходники и оба способа
внесения наших изменений предоставлены. При использовании --engine-dist нужно
распространять соответствующие исходники именно своего изменённого движка.

## Локальные изменения

Изменён только путь подготовки и диагностики разметки PresentationEditor:
layout getJSON заменён вызовом obsidopfLayoutSnapshot, а шесть существующих числовых
сообщений Perf передаются obsidopfRecordLayoutStage. Помощники определены в
layout-performance.js оболочки. Экспорт DOCX, буфер обмена и планировщик движка
этими изменениями не переписывались. Исходные изменения помечены датой 2026-11-15;
эквивалентный патч опубликованного bundle применяется с 2026-11-06.
