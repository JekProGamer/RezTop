# RezTop — CounterStrikeSharp

## Состав
- `RezTop.cs` — исходный код.
- `RezTop.csproj` — проект .NET 8 с CounterStrikeSharp.API 1.0.380.
- `maplist.txt` — последовательность карт.
- `messages.json` — префикс и все чат-сообщения с цветами.

## Сборка
Требуется .NET 8 SDK.

```bash
dotnet restore
dotnet build -c Release
```

DLL будет в `bin/Release/net8.0/RezTop.dll`.

## Установка
1. Установи Metamod:Source и CounterStrikeSharp.
2. Создай папку `game/csgo/addons/counterstrikesharp/plugins/RezTop/`.
3. Скопируй туда `RezTop.dll`, `maplist.txt` и `messages.json`.
4. Перезапусти сервер или загрузи плагин командой `css_plugins load RezTop`.

## Важно
Плагин использует `mp_ignore_round_win_conditions 1` и сам определяет победителя по живым игрокам CT/T.
Игрок с нулём жизней не переводится в команду Spectator: он остаётся в своей команде мёртвым до следующего раунда.

Это стартовая реализация под CounterStrikeSharp API. Её нужно проверить на тестовом сервере с тем же билдом CS2/CounterStrikeSharp, что установлен у тебя. Особенно проверь поведение `TerminateRound` при используемом deathmatch-конфиге и настройки режима, которые могут автоматически запускать/завершать раунд.


## Ротация Workshop-карт в RezTop

В `maplist.txt` можно смешивать стандартные карты и Workshop-карты:

```text
de_mirage
workshop:aim_map
workshop:surf_beginner
de_dust2
```

Для Workshop указывай **имя карты**, которое показывает серверная команда `ds_workshop_listmaps`, а не ID страницы и не ID коллекции. Карты должны быть заранее загружены/доступны серверу (например, через настроенную Workshop-коллекцию). RezTop переключает на них командой `ds_workshop_changelevel <имя>`; обычные карты — через `changelevel <имя>`. После последней строки ротация возвращается к первой.
