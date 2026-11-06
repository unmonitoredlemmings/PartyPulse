namespace PartyPulse.Core;

public sealed record HealthEvaluation(
    IReadOnlyList<PartyMemberSnapshot> MonitoredMembers,
    IReadOnlyList<PartyMemberSnapshot> LowMembers)
{
    public bool ShouldAlert => LowMembers.Count > 0;
}
