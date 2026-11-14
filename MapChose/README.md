# MapChose

Небольшой плагин CounterStrikeSharp для CS2, который циклически переключает сервер между двумя Workshop-картами.

## Настройка

Отредактируй `maplist.txt`. В нём должно быть ровно две строки с именами карт, например:

```text
dm_nixware_mirage_d
dm_nixware_fysnow_d
```

Указывай имя карты, которое сервер распознаёт для `ds_workshop_changelevel`, а не ID коллекции или страницы Workshop. Обе карты должны быть доступны серверу через Workshop.

## Сборка

Требуется .NET 8 SDK:

```bash
dotnet restore MapChose.csproj
dotnet build MapChose.csproj -c Release
```

Готовая DLL: `bin/Release/net8.0/MapChose.dll`.

## Установка

Скопируй `MapChose.dll` и `maplist.txt` в:

```text
game/csgo/addons/counterstrikesharp/plugins/MapChose/
```

Перезапусти сервер или загрузи плагин через CounterStrikeSharp.

## Работа

При старте карты плагин запоминает её имя. При завершении карты выбирает следующую из двух строк и вызывает:

```text
ds_workshop_changelevel <имя_карты>
```

После второй карты цикл начинается с первой.
