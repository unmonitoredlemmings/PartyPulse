namespace PartyPulse.Core;

public sealed record MonitorSettings(
    bool Enabled,
    MonitorMode Mode,
    int ThresholdPercent,
    string? PrimaryKey,
    string? SecondaryKey,
    bool IgnoreDead = false);
