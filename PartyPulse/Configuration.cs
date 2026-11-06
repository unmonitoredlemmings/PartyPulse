using Dalamud.Configuration;
using PartyPulse.Core;

namespace PartyPulse;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public MonitorMode Mode { get; set; } = MonitorMode.Single;
    public int ThresholdPercent { get; set; } = 50;
    public string? PrimaryKey { get; set; }
    public string? PrimaryName { get; set; }
    public string? SecondaryKey { get; set; }
    public string? SecondaryName { get; set; }
    public bool IgnoreDead { get; set; }
    public string SoundPath { get; set; } = string.Empty;
    public float Volume { get; set; } = 0.5f;
    public bool RecoveryEnabled { get; set; } = false;
    public string RecoverySoundPath { get; set; } = string.Empty;
    public float RecoveryVolume { get; set; } = 0.5f;
    public bool OpenOnLoad { get; set; } = true;

    public void Normalize()
    {
        ThresholdPercent = Math.Clamp(ThresholdPercent, 0, 100);
        Volume = float.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : 0.5f;
        RecoveryVolume = float.IsFinite(RecoveryVolume) ? Math.Clamp(RecoveryVolume, 0, 1) : 0.5f;
        if (!Enum.IsDefined(Mode)) Mode = MonitorMode.Single;
        SoundPath ??= string.Empty;
        RecoverySoundPath ??= string.Empty;
    }

    public MonitorSettings ToSettings() => new(Enabled, Mode, ThresholdPercent,
        PrimaryKey, SecondaryKey, IgnoreDead);
}
