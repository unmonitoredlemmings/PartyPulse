namespace PartyPulse.Core;

/// <summary>
/// Detects per-member recovery on consecutive fresh party samples. This class is
/// used from the sampling thread; call Reset when monitoring is paused or stopped.
/// </summary>
public sealed class RecoveryTracker
{
    private readonly HashSet<string> previousLowKeys = new(StringComparer.Ordinal);
    private MonitorSettings? previousSettings;

    /// <summary>
    /// Returns each selected member whose valid HP crossed from at/below the
    /// threshold to strictly above it. The first sample only establishes a baseline.
    /// Changes to monitoring settings also establish a new baseline.
    /// </summary>
    public IReadOnlyList<PartyMemberSnapshot> Update(
        IReadOnlyList<PartyMemberSnapshot> members,
        MonitorSettings settings,
        bool recoveryEnabled)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled || !recoveryEnabled)
        {
            Reset();
            return Array.Empty<PartyMemberSnapshot>();
        }

        if (previousSettings != settings)
        {
            previousLowKeys.Clear();
            previousSettings = settings;
        }

        var evaluation = HealthEvaluator.Evaluate(members, settings);
        var threshold = (ulong)Math.Clamp(settings.ThresholdPercent, 0, 100);
        var recovered = new List<PartyMemberSnapshot>();

        foreach (var member in evaluation.MonitoredMembers)
        {
            if (previousLowKeys.Contains(member.Key) &&
                member.IsAvailable && member.MaxHp > 0 &&
                (!settings.IgnoreDead || !member.IsDead) &&
                (ulong)member.CurrentHp * 100 > (ulong)member.MaxHp * threshold)
            {
                recovered.Add(member);
            }
        }

        // Replace, rather than merge, the history. A missing, unavailable, or
        // ignored KO member cannot produce a retroactive recovery on reappearance.
        previousLowKeys.Clear();
        foreach (var member in evaluation.LowMembers)
            previousLowKeys.Add(member.Key);

        return recovered.AsReadOnly();
    }

    public void Reset()
    {
        previousLowKeys.Clear();
        previousSettings = null;
    }
}
