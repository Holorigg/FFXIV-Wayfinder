# Wayfinder

Плагин Dalamud для FINAL FANTASY XIV: рисует пеший путь на земле, как GPS, к флажку на карте или к цели квеста.

- Режимы: **флажок на карте** (Ctrl + правый клик по карте), **квест** (сюжет автоматически или любой взятый квест),
  **ближайший маркетборд** и **ближайший ремонтник** (кнопки в окне).
- Ведёт через выходы между зонами, выбирая выход по реальной длине пути.
- Подсказывает городской аэтернет, если перелёт заметно короче, и телепорт с ценой, если идти далеко.
- Прячет путь за стенами и в бою, всё настраивается в окне.
- Только рисует: персонажа не двигает.

## Установка

1. Нужен **vnavmesh**. В игре: `/xlsettings` → Experimental → Custom Plugin Repositories → добавить
   `https://puni.sh/api/repository/veyn`, сохранить, затем в `/xlplugins` установить **vnavmesh**.
2. Туда же добавить репозиторий Wayfinder:
   `https://raw.githubusercontent.com/Holorigg/FFXIV-Wayfinder/main/repo.json`
3. В `/xlplugins` найти **Wayfinder** и установить.

## Использование

`/way` открывает окно: режим, выбор квеста, маршрут, внешний вид, язык (RU / EN).

Быстрые команды: `/way flag`, `/way market`, `/way repair`, `/way quest [название]`, `/way next`, `/way on`, `/way off`, `/way toggle`.

## Важно

Сторонние плагины Dalamud нарушают пользовательское соглашение FINAL FANTASY XIV. Используете на свой риск.

---

**English.** Dalamud plugin that draws a GPS-style walking path on the ground to your map flag or quest objective,
through zone exits, with city aethernet and teleport hints. It only draws and never moves your character.
Requires vnavmesh (`https://puni.sh/api/repository/veyn`). Add
`https://raw.githubusercontent.com/Holorigg/FFXIV-Wayfinder/main/repo.json` as a custom repository, install Wayfinder,
then type `/way`.
