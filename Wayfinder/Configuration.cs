using System;
using System.Numerics;
using Dalamud.Configuration;

namespace Wayfinder;

// Public fields so the settings window can bind to them with ref.
// Values are stored as numbers: 2 and 3 were "market board" and "mender" before Nearest covered every landmark.
public enum WayMode
{
    Flag = 0,
    Quest = 1,
    Nearest = 2,
    LegacyMender = 3,
    AetherCurrent = 4,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled = true;
    public Language Language = Language.Russian;
    public WayMode Mode = WayMode.Flag;
    public ushort QuestId; // 0 = follow the main scenario automatically
    public LandmarkKind Place = LandmarkKind.MarketBoard;
    public Vector4 Color = new(0.45f, 0.85f, 1f, 1f);
    public float Opacity = 0.85f;
    public float Range = 60f;
    public float Width = 0.7f;
    public bool ShowRibbon = true;
    public bool ShowChevrons = true;
    public bool Animate = true;
    public bool HideBehindWalls = true;
    public bool ShowHud = true;
    public bool ShowTeleportHint = true;
    public bool HideInCombat = true;
    public bool HideInDuties;
    public float TeleportMinWalk = 400f;
    public bool ShowOnMinimap;
    public bool ShowEta = true;
    public bool MountHint = true;
    public float MountHintDistance = 150f;

    public bool ExitByPathLength = true;
    public bool UseAethernet = true;
    public float AethernetMinSaving = 80f;
    public float AethernetOverhead = 40f;
    public bool UseTransports = true;
    public bool FlyWhenAble = true;
}
