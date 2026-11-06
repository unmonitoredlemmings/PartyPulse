namespace PartyPulse.Core;

public static class HealthEvaluator
{
    /// <summary>
    /// Evaluates one fresh snapshot. The alert stays active while any monitored,
    /// available member is at or below the threshold. Missing members are never
    /// replaced by the person now occupying their former party slot.
    /// </summary>
    public static HealthEvaluation Evaluate(
        IReadOnlyList<PartyMemberSnapshot> members,
        MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(settings);

        var monitored = new List<PartyMemberSnapshot>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        void AddSelected(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || !seenKeys.Add(key))
                return;

            foreach (var member in members)
            {
                if (!string.Equals(member.Key, key, StringComparison.Ordinal))
                    continue;

                monitored.Add(member);
                return;
            }
        }

        switch (settings.Mode)
        {
            case MonitorMode.Single:
                AddSelected(settings.PrimaryKey);
                break;
            case MonitorMode.Duo:
                AddSelected(settings.PrimaryKey);
                AddSelected(settings.SecondaryKey);
                break;
            case MonitorMode.AllParty:
                foreach (var member in members)
                {
                    if (seenKeys.Add(member.Key))
                        monitored.Add(member);
                }

                break;
            default:
                // A stale or corrupt configuration must not select an unintended target.
                break;
        }

        if (!settings.Enabled)
            return new HealthEvaluation(monitored.AsReadOnly(), Array.Empty<PartyMemberSnapshot>());

        var threshold = (ulong)Math.Clamp(settings.ThresholdPercent, 0, 100);
        var low = monitored.Where(member =>
                member.IsAvailable &&
                member.MaxHp > 0 &&
                (!settings.IgnoreDead || !member.IsDead) &&
                // Exact comparison: display rounding must not alter the alert boundary.
                // ulong also avoids overflow for valid uint health values.
                (ulong)member.CurrentHp * 100 <= (ulong)member.MaxHp * threshold)
            .ToArray();

        return new HealthEvaluation(monitored.AsReadOnly(), Array.AsReadOnly(low));
    }
}
