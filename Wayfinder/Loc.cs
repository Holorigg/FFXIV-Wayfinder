using System.Collections.Generic;
using System.Globalization;

namespace Wayfinder;

public enum Language
{
    English,
    Russian,
}

// Plugin text in two languages. Place, quest and NPC names come from the game client as-is.
internal static class Loc
{
    public static Language Current = Language.Russian;

    private static readonly Dictionary<string, (string En, string Ru)> Strings = new()
    {
        // panel
        ["panel"] = ("{0}  -  {1:0} y", "{0}  ·  {1:0} м"),
        ["flag"] = ("Flag", "Флажок"),
        ["market"] = ("Market Board", "Маркетборд"),
        ["mender"] = ("Mender", "Ремонтник"),
        ["pick-up"] = ("{0} (pick up)", "{0} (взять квест)"),
        ["step.shard.walk"] = ("Walk to the aethernet shard \"{0}\", then pick \"{1}\" (about {2:0} y shorter)",
                               "Иди к кристаллу аэтернета «{0}», там выбери «{1}» (короче примерно на {2:0} м)"),
        ["step.shard.here"] = ("You are at the shard: pick \"{0}\" in the aethernet menu",
                               "Ты у кристалла: в меню аэтернета выбери «{0}»"),
        ["step.exit"] = ("Walk to the way out to \"{0}\"", "Иди к выходу в «{0}»"),
        ["step.teleport"] = ("or teleport to \"{0}\" for {1} gil", "или телепорт в «{0}» за {1} гил"),

        // statuses
        ["status.place-flag"] = ("Wayfinder: place a flag on the map (Ctrl + right click)",
                                 "Wayfinder: поставь флажок на карте (Ctrl + правый клик)"),
        ["status.no-market"] = ("Wayfinder: no market board reachable on foot", "Wayfinder: пешком до маркетборда не дойти"),
        ["status.no-mender"] = ("Wayfinder: no mender reachable on foot", "Wayfinder: пешком до ремонтника не дойти"),
        ["status.no-quest-target"] = ("Wayfinder: no quest target", "Wayfinder: нет цели квеста"),
        ["status.no-msq"] = ("Wayfinder: no main scenario quest found", "Wayfinder: сюжетный квест не найден"),
        ["status.quest-gone"] = ("Wayfinder: that quest is no longer in your journal", "Wayfinder: этого квеста больше нет в журнале"),
        ["status.no-location"] = ("Wayfinder: no map location for the current step of {0}",
                                  "Wayfinder: у текущего шага «{0}» нет точки на карте"),
        ["status.no-route"] = ("Wayfinder: no known walking route to that area", "Wayfinder: пешего пути в ту зону не знаю"),
        ["status.no-vnavmesh"] = ("Wayfinder: vnavmesh is not loaded", "Wayfinder: vnavmesh не загружен"),
        ["status.mesh-loading"] = ("Wayfinder: waiting for vnavmesh to load this zone...", "Wayfinder: vnavmesh загружает эту зону..."),
        ["status.off-mesh"] = ("Wayfinder: target is not on the navmesh", "Wayfinder: цель не на навмеше"),
        ["status.looking"] = ("Wayfinder: looking for a path...", "Wayfinder: ищу путь..."),
        ["status.no-path"] = ("Wayfinder: no path found", "Wayfinder: путь не найден"),
        ["status.pathfind-failed"] = ("Wayfinder: vnavmesh pathfinding failed", "Wayfinder: vnavmesh не смог проложить путь"),

        // chat
        ["chat.flag"] = ("following the map flag", "веду к флажку на карте"),
        ["chat.market"] = ("leading to the nearest market board", "веду к ближайшему маркетборду"),
        ["chat.mender"] = ("leading to the nearest mender", "веду к ближайшему ремонтнику"),
        ["chat.msq"] = ("following the main scenario: {0}", "веду по сюжету: {0}"),
        ["chat.msq-none"] = ("following the main scenario", "веду по сюжету"),
        ["chat.quest"] = ("following {0}", "веду по квесту: {0}"),
        ["chat.quest-gone"] = ("following a quest that is no longer in your journal", "выбранного квеста больше нет в журнале"),
        ["chat.on"] = ("on", "включён"),
        ["chat.off"] = ("off", "выключен"),
        ["chat.no-match"] = ("no quest in your journal matches \"{0}\"", "в журнале нет квеста, похожего на «{0}»"),
        ["chat.help"] = ("/way opens the window. Shortcuts: flag, market, repair, quest [name], next, on, off, toggle.",
                         "/way открывает окно. Быстрые команды: flag, market, repair, quest [название], next, on, off, toggle."),
        ["chat.teleport-failed"] = ("teleport failed", "телепорт не удался"),

        // window
        ["ui.show"] = ("Show path", "Показывать путь"),
        ["ui.language"] = ("Language", "Язык"),
        ["ui.lead"] = ("Lead me to", "Куда вести"),
        ["ui.mode.flag"] = ("Map flag", "Флажок на карте"),
        ["ui.mode.quest"] = ("Quest", "Квест"),
        ["ui.mode.market"] = ("Market Board", "Маркетборд"),
        ["ui.mode.mender"] = ("Mender", "Ремонтник"),
        ["ui.mender-button"] = ("Nearest mender", "К ближайшему ремонтнику"),
        ["ui.mender-tip"] = ("Picks the closest mender (gear repair NPC) you can walk to; /way repair does the same.",
                             "Ведёт к ближайшему ремонтнику, до которого можно дойти; то же делает /way repair."),
        ["ui.market-button"] = ("Nearest market board", "К ближайшему маркетборду"),
        ["ui.market-tip"] = ("Picks the closest market board you can walk to; /way market does the same.",
                             "Ведёт к ближайшему маркетборду, до которого можно дойти; то же делает /way market."),
        ["ui.flag-tip"] = ("Ctrl + right click on the map to place a flag.", "Ctrl + правый клик по карте ставит флажок."),
        ["ui.msq-auto"] = ("Main scenario (auto)", "Сюжет (авто)"),
        ["ui.msq-auto-named"] = ("Main scenario (auto): {0}", "Сюжет (авто): {0}"),
        ["ui.pick-up-suffix"] = (" - pick up", " — взять квест"),
        ["ui.no-quests"] = ("No quests in your journal.", "В журнале нет квестов."),
        ["ui.next-tip"] = ("/way next cycles through these.", "/way next перебирает их по кругу."),
        ["ui.now"] = ("Now: {0}", "Сейчас: {0}"),
        ["ui.now-none"] = ("Now: nothing to follow", "Сейчас: вести некуда"),
        ["ui.teleport-row"] = ("Teleport: {0} - {1} gil", "Телепорт: {0} — {1} гил"),
        ["ui.teleport-button"] = ("Teleport", "Телепорт"),
        ["ui.routing"] = ("Routing", "Маршрут"),
        ["ui.exit-by-path"] = ("Pick the zone exit by walking distance", "Выбирать выход из зоны по длине пешего пути"),
        ["ui.exit-by-path-tip"] = ("Measures the real walk to each exit with vnavmesh instead of the straight line.",
                                   "Меряет через vnavmesh реальный путь до каждого выхода, а не прямую."),
        ["ui.aethernet"] = ("Use city aethernet shortcuts", "Подсказывать городской аэтернет"),
        ["ui.aethernet-tip"] = ("Leads to an attuned aethernet shard when hopping is clearly shorter than walking. Free.",
                                "Ведёт к открытому кристаллу аэтернета, если перелёт заметно короче ходьбы. Бесплатно."),
        ["ui.aethernet-min"] = ("Only if it saves (yalms)", "Только если короче на (м)"),
        ["ui.aethernet-cost"] = ("A hop costs like walking (yalms)", "Перелёт «стоит» как ходьба (м)"),
        ["ui.aethernet-cost-tip"] = ("Raise it if loading screens annoy you more than walking.",
                                     "Подними, если загрузки бесят больше, чем ходьба."),
        ["ui.teleport-hint"] = ("Suggest a teleport when the target is in another zone", "Подсказывать телепорт, если цель в другой зоне"),
        ["ui.teleport-hint-tip"] = ("Only a suggestion with the gil price; the walking path is always drawn.",
                                    "Только подсказка с ценой; пеший путь рисуется всегда."),
        ["ui.teleport-min"] = ("Only if the walk is over (yalms)", "Только если идти больше (м)"),
        ["ui.teleport-min-tip"] = ("Rough walk: measured way to the zone exit plus about 150 yalms on the other side.",
                                   "Примерно: путь до выхода из зоны плюс ~150 м с другой стороны."),
        ["ui.look"] = ("Look", "Вид"),
        ["ui.colour"] = ("Colour", "Цвет"),
        ["ui.opacity"] = ("Opacity", "Прозрачность"),
        ["ui.range"] = ("Range (yalms)", "Дальность (м)"),
        ["ui.width"] = ("Width (yalms)", "Ширина (м)"),
        ["ui.ribbon"] = ("Ground ribbon", "Лента на земле"),
        ["ui.arrows"] = ("Arrows", "Стрелки"),
        ["ui.animate"] = ("Animate arrows", "Анимация стрелок"),
        ["ui.walls"] = ("Hide behind walls", "Прятать за стенами"),
        ["ui.panel"] = ("Distance panel", "Панель с расстоянием"),
        ["ui.combat"] = ("Hide in combat", "Прятать в бою"),
        ["ui.combat-tip"] = ("Keeps the floor clear for the game's own attack markers.", "Чтобы не перекрывать маркеры атак на полу."),
        ["ui.duties"] = ("Hide in dungeons and other duties", "Прятать в данжах и других заданиях"),
    };

    public static string T(string key, params object[] args)
    {
        if (!Strings.TryGetValue(key, out var pair))
            return key;
        var format = Current == Language.Russian ? pair.Ru : pair.En;
        return args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
