namespace PartyPulse.Core;

/// <summary>A copy of a party member's health, independent of the game API.</summary>
public sealed record PartyMemberSnapshot(
    string Key,
    string Name,
    uint CurrentHp,
    uint MaxHp,
    bool IsAvailable = true)
{
    public double? HpPercent => IsAvailable && MaxHp > 0
        ? 100.0 * CurrentHp / MaxHp
        : null;

    public bool IsDead => IsAvailable && MaxHp > 0 && CurrentHp == 0;
}
