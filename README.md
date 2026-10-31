# Wayfinder

Плагин Dalamud для FINAL FANTASY XIV: рисует пеший путь на земле, как GPS, к флажку на карте или к цели квеста.

- Режимы: **флажок на карте** (Ctrl + правый клик по карте), **квест** (сюжет автоматически или любой взятый квест),
  **ближайшее место** и **потоки эфира**.
- Ближайшее место — кнопкой в окне: маркетборд, ремонтник, колокол ретейнеров, гостиница, почта, доска охоты,
  чокобо-извозчик, гильдия твоего класса, неоткрытый эфирит.
- Режимы «все квесты» (ближайшая цель из журнала), «к участнику группы» и «NPC по имени» (поиск).
- Шаг квеста с несколькими целями («поговори с тремя») ведёт по ним по очереди; остальные цели видны маркерами.
- Ведёт через выходы между зонами, выбирая выход по реальной длине пути, и через паромы, дирижабли и лифты,
  которые у тебя открыты (с ценой).
- Подсказывает городской аэтернет, если перелёт заметно короче, и телепорт с ценой, если идти далеко.
- Время в пути; подсказка сесть на маунта, если он у тебя есть. На маунте в зоне с полётами путь идёт по воздуху.
- По желанию — маршрут на миникарте и большой карте, подсказка по наградам квестов (обе выключены по умолчанию).
- Размер и положение панели настраиваются.
- Прячет путь за стенами и в бою, всё настраивается в окне.
- Только рисует: персонажа не двигает.

## Установка

1. Нужен **vnavmesh**. В игре: `/xlsettings` → Experimental → Custom Plugin Repositories → добавить
   `https://puni.sh/api/repository/veyn`, сохранить, затем в `/xlplugins` установить **vnavmesh**.
2. Туда же добавить репозиторий Wayfinder:
   `https://raw.githubusercontent.com/Holorigg/FFXIV-Wayfinder/main/repo.json`
3. В `/xlplugins` найти **Wayfinder** и установить.

## Использование

`/way` открывает окно: режим, выбор квеста, ближайшие места, маршрут, внешний вид, язык (RU / EN).

Быстрые команды: `/way flag`, `/way quest [название]`, `/way next`, `/way skip` (следующая цель шага),
`/way market`, `/way repair`, `/way bell`, `/way inn`, `/way mail`, `/way hunt`, `/way chocobo`, `/way guild`,
`/way attune`, `/way all`, `/way follow`, `/way currents`, `/way on`, `/way off`, `/way toggle`.

## Важно

Сторонние плагины Dalamud нарушают пользовательское соглашение FINAL FANTASY XIV. Используете на свой риск.

---

**English.** Dalamud plugin that draws a GPS-style path on the ground to your map flag, quest objective (several
spots of one step in turn), the nearest service (market board, mender, summoning bell, inn, mail, hunt board,
chocobo porter, your class guild) or the aether currents of a zone. Routes through zone exits, unlocked ferries,
airships and lifts, with city aethernet and teleport hints, travel time, a mount hint, flight paths when you can fly,
and an optional minimap overlay. It only draws and never moves your character.
Requires vnavmesh (`https://puni.sh/api/repository/veyn`). Add
`https://raw.githubusercontent.com/Holorigg/FFXIV-Wayfinder/main/repo.json` as a custom repository, install Wayfinder,
then type `/way`.
