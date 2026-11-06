using PartyPulse.Core;

internal static class RecoveryTrackerChecks
{
    private static readonly MonitorSettings Single = new(true, MonitorMode.Single, 50, "a", null);

    public static readonly (string Name, Action Run)[] All =
    [
        ("Recovery starts with a silent baseline", () =>
        {
            var tracker = new RecoveryTracker();
            Keys(tracker.Update([Member("a", 80)], Single, true));
            Keys(tracker.Update([Member("a", 90)], Single, true));
        }),
        ("Recovery crosses exact inclusive boundary without display rounding", () =>
        {
            var tracker = new RecoveryTracker();
            Keys(tracker.Update([Member("a", 5000, 10000)], Single, true));
            Keys(tracker.Update([Member("a", 5000, 10000)], Single, true));
            Keys(tracker.Update([Member("a", 5001, 10000)], Single, true), "a");
        }),
        ("Recovery is one-shot and rearms only after another low-health sample", () =>
        {
            var tracker = new RecoveryTracker();
            Keys(tracker.Update([Member("a", 20)], Single, true));
            Keys(tracker.Update([Member("a", 40)], Single, true));
            Keys(tracker.Update([Member("a", 80)], Single, true), "a");
            Keys(tracker.Update([Member("a", 90)], Single, true));
            Keys(tracker.Update([Member("a", 100)], Single, true));
            Keys(tracker.Update([Member("a", 10)], Single, true));
            Keys(tracker.Update([Member("a", 51)], Single, true), "a");
        }),
        ("Recovery reset suppresses stale transitions after logout or pause", () =>
        {
            var tracker = new RecoveryTracker();
            tracker.Update([Member("a", 20)], Single, true);
            tracker.Reset();
            Keys(tracker.Update([Member("a", 80)], Single, true));
            tracker.Update([Member("a", 30)], Single, true);
            Keys(tracker.Update([Member("a", 80)], Single, true), "a");
        }),
        ("Disabled monitoring discards recovery history", () =>
        {
            var tracker = new RecoveryTracker();
            tracker.Update([Member("a", 20)], Single, true);
            Keys(tracker.Update([Member("a", 20)], Single with { Enabled = false }, true));
            Keys(tracker.Update([Member("a", 80)], Single, true));
        }),
        ("Enabling recovery sound never reports a retroactive recovery", () =>
        {
            var tracker = new RecoveryTracker();
            Keys(tracker.Update([Member("a", 20)], Single, false));
            Keys(tracker.Update([Member("a", 80)], Single, true));
            tracker.Update([Member("a", 20)], Single, true);
            Keys(tracker.Update([Member("a", 20)], Single, false));
            Keys(tracker.Update([Member("a", 80)], Single, true));
            tracker.Update([Member("a", 20)], Single, true);
            Keys(tracker.Update([Member("a", 80)], Single, true), "a");
        }),
        ("Threshold edits establish a new recovery baseline", () =>
        {
            var tracker = new RecoveryTracker();
            tracker.Update([Member("a", 40)], Single, true);
            var lowerThreshold = Single with { ThresholdPercent = 30 };
            Keys(tracker.Update([Member("a", 40)], lowerThreshold, true));
            tracker.Update([Member("a", 20)], lowerThreshold, true);
            Keys(tracker.Update([Member("a", 31)], lowerThreshold, true), "a");
        }),
        ("Primary selection changes cannot reuse recovery history", () =>
        {
            var tracker = new RecoveryTracker();
            tracker.Update([Member("a", 20), Member("b", 20)], Single, true);
            var changed = Single with { PrimaryKey = "b" };
            Keys(tracker.Update([Member("a", 80), Member("b", 80)], changed, true));
            tracker.Update([Member("a", 20), Member("b", 20)], changed, true);
            Keys(tracker.Update([Member("a", 80), Member("b", 80)], Single, true));
        }),
        ("Secondary selection changes reset the duo recovery baseline", () =>
        {
            var tracker = new RecoveryTracker();
            var duo = Single with { Mode = MonitorMode.Duo, SecondaryKey = "b" };
            tracker.Update([Member("a", 20), Member("b", 20), Member("c", 20)], duo, true);
            Keys(tracker.Update([Member("a", 80), Member("b", 80), Member("c", 80)],
                duo with { SecondaryKey = "c" }, true));
        }),
        ("Mode and ignore-KO setting changes reset recovery history", () =>
        {
            var tracker = new RecoveryTracker();
            tracker.Update([Member("a", 20)], Single, true);
            var all = Single with { Mode = MonitorMode.AllParty };
            Keys(tracker.Update([Member("a", 80)], all, true));
            tracker.Update([Member("a", 20)], all, true);
            Keys(tracker.Update([Member("a", 80)], all with { IgnoreDead = true }, true));
        }),
        ("Missing and unavailable members cannot recover retroactively", () =>
        {
            PartyMemberSnapshot[][] interruptions =
            [
                [],
                [Member("a", 0) with { IsAvailable = false }],
                [Member("a", 0, 0)],
            ];
            foreach (var interrupted in interruptions)
            {
                var tracker = new RecoveryTracker();
                tracker.Update([Member("a", 20)], Single, true);
                Keys(tracker.Update(interrupted, Single, true));
                Keys(tracker.Update([Member("a", 80)], Single, true));
            }
        }),
        ("Duo recovery is per member even while another target stays low", () =>
        {
            var tracker = new RecoveryTracker();
            var duo = Single with { Mode = MonitorMode.Duo, SecondaryKey = "b" };
            tracker.Update([Member("a", 20), Member("b", 20)], duo, true);
            Keys(tracker.Update([Member("a", 80), Member("b", 20)], duo, true), "a");
            Keys(tracker.Update([Member("a", 90), Member("b", 80)], duo, true), "b");
            Keys(tracker.Update([Member("a", 100), Member("b", 90)], duo, true));
        }),
        ("Entire party reports simultaneous recoveries once and ignores newly joined healthy members", () =>
        {
            var tracker = new RecoveryTracker();
            var all = Single with { Mode = MonitorMode.AllParty };
            tracker.Update([Member("a", 20), Member("b", 30), Member("c", 40)], all, true);
            Keys(tracker.Update([Member("c", 90), Member("b", 80), Member("a", 20), Member("d", 80)],
                all, true), "c", "b");
            Keys(tracker.Update([Member("a", 20), Member("b", 90), Member("c", 100), Member("d", 90)],
                all, true));
        }),
        ("Duplicate duo selections produce one recovery event", () =>
        {
            var tracker = new RecoveryTracker();
            var duplicate = Single with { Mode = MonitorMode.Duo, SecondaryKey = "a" };
            tracker.Update([Member("a", 20)], duplicate, true);
            Keys(tracker.Update([Member("a", 80)], duplicate, true), "a");
        }),
        ("Ignored KO state is never a recovery and breaks recovery history", () =>
        {
            var tracker = new RecoveryTracker();
            var ignoreKo = Single with { IgnoreDead = true };
            tracker.Update([Member("a", 20)], ignoreKo, true);
            Keys(tracker.Update([Member("a", 0)], ignoreKo, true));
            Keys(tracker.Update([Member("a", 80)], ignoreKo, true));
            tracker.Update([Member("a", 0)], ignoreKo, true);
            Keys(tracker.Update([Member("a", 20)], ignoreKo, true));
            Keys(tracker.Update([Member("a", 80)], ignoreKo, true), "a");
        }),
        ("KO members participate in recovery when ignoring KO is disabled", () =>
        {
            var tracker = new RecoveryTracker();
            tracker.Update([Member("a", 0)], Single, true);
            Keys(tracker.Update([Member("a", 80)], Single, true), "a");
        }),
        ("Recovery comparisons remain exact at uint health limits", () =>
        {
            var tracker = new RecoveryTracker();
            tracker.Update([Member("a", uint.MaxValue / 2, uint.MaxValue)], Single, true);
            Keys(tracker.Update([Member("a", uint.MaxValue / 2 + 1, uint.MaxValue)], Single, true), "a");
        }),
    ];

    private static PartyMemberSnapshot Member(string key, uint hp, uint maxHp = 100) =>
        new(key, key, hp, maxHp);

    private static void Keys(IReadOnlyList<PartyMemberSnapshot> members, params string[] expected)
    {
        var actual = members.Select(member => member.Key).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected recovered [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }
}
