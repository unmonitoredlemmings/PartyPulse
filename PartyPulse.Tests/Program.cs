using PartyPulse.Core;

var tests = new (string Name, Action Run)[]
{
    ("Single target alerts at the exact threshold", () =>
    {
        var result = Evaluate([Member("a", 50)], MonitorMode.Single, "a");
        Require(result.ShouldAlert, "50/100 must alert at 50%.");
        EqualKeys(result.LowMembers, "a");
    }),
    ("Single target alerts below the threshold", () =>
    {
        Require(Evaluate([Member("a", 49)], MonitorMode.Single, "a").ShouldAlert,
            "49/100 must alert at 50%.");
    }),
    ("Fraction above threshold does not alert even when rounded display matches", () =>
    {
        var result = Evaluate([Member("a", 5001, 10000)], MonitorMode.Single, "a");
        Require(!result.ShouldAlert, "50.01% is above 50%.");
    }),
    ("Fraction below threshold alerts", () =>
    {
        Require(Evaluate([Member("a", 4999, 10000)], MonitorMode.Single, "a").ShouldAlert,
            "49.99% must alert at 50%.");
    }),
    ("Single target ignores other low members", () =>
    {
        var result = Evaluate([Member("a", 80), Member("b", 10)], MonitorMode.Single, "a");
        Require(!result.ShouldAlert, "Unselected b must not trigger the alarm.");
        EqualKeys(result.MonitoredMembers, "a");
    }),
    ("Duo alerts when only the second selected member is low", () =>
    {
        var result = Evaluate([Member("a", 90), Member("b", 50)], MonitorMode.Duo, "a", "b");
        Require(result.ShouldAlert, "The second selected target must trigger the alarm.");
        EqualKeys(result.LowMembers, "b");
    }),
    ("Duo remains active until both selected members recover", () =>
    {
        Require(Evaluate([Member("a", 10), Member("b", 20)], MonitorMode.Duo, "a", "b").ShouldAlert,
            "Two low members must alert.");
        Require(Evaluate([Member("a", 80), Member("b", 50)], MonitorMode.Duo, "a", "b").ShouldAlert,
            "One recovered member must not silence the other.");
        Require(!Evaluate([Member("a", 80), Member("b", 51)], MonitorMode.Duo, "a", "b").ShouldAlert,
            "The alarm must stop when both members are above 50%.");
    }),
    ("Duo does not monitor an unselected third member", () =>
    {
        var result = Evaluate([Member("a", 80), Member("b", 90), Member("c", 10)],
            MonitorMode.Duo, "a", "b");
        Require(!result.ShouldAlert, "Unselected c must not trigger the alarm.");
    }),
    ("Duo deduplicates the same selected identity", () =>
    {
        var result = Evaluate([Member("a", 20)], MonitorMode.Duo, "a", "a");
        EqualKeys(result.MonitoredMembers, "a");
        EqualKeys(result.LowMembers, "a");
    }),
    ("All-party mode monitors everyone without selections", () =>
    {
        var result = Evaluate([Member("a", 80), Member("b", 20), Member("c", 50)], MonitorMode.AllParty);
        EqualKeys(result.MonitoredMembers, "a", "b", "c");
        EqualKeys(result.LowMembers, "b", "c");
    }),
    ("Party order changes do not retarget the selected identity", () =>
    {
        var first = Evaluate([Member("a", 80), Member("b", 20)], MonitorMode.Single, "a");
        var reordered = Evaluate([Member("b", 20), Member("a", 80)], MonitorMode.Single, "a");
        Require(!first.ShouldAlert && !reordered.ShouldAlert, "A reordered party must retain selected a.");
        EqualKeys(reordered.MonitoredMembers, "a");
    }),
    ("Members with identical display names are distinguished by identity", () =>
    {
        var result = Evaluate([
            new PartyMemberSnapshot("world1:name", "Same Name", 80, 100),
            new PartyMemberSnapshot("world2:name", "Same Name", 20, 100),
        ], MonitorMode.Single, "world1:name");
        Require(!result.ShouldAlert, "Same-name member from another world must not replace the selection.");
    }),
    ("Missing or blank selection does not fall back to another member", () =>
    {
        foreach (var key in new string?[] { "missing", null, "", " " })
        {
            var result = Evaluate([Member("replacement", 10)], MonitorMode.Single, key);
            Require(!result.ShouldAlert && result.MonitoredMembers.Count == 0,
                "A missing selection must stay unselected.");
        }
    }),
    ("Missing primary does not prevent monitoring an available secondary", () =>
    {
        var result = Evaluate([Member("b", 20)], MonitorMode.Duo, "missing", "b");
        EqualKeys(result.LowMembers, "b");
    }),
    ("Unavailable and zero-max-health members do not produce false alarms", () =>
    {
        var unavailable = Member("a", 0) with { IsAvailable = false };
        var unknown = Member("b", 0, 0);
        var result = Evaluate([unavailable, unknown], MonitorMode.AllParty);
        Require(!result.ShouldAlert, "Unknown health must not be interpreted as 0%.");
        EqualKeys(result.MonitoredMembers, "a", "b");
        Require(unavailable.HpPercent is null && unknown.HpPercent is null,
            "Unavailable health must display as unknown.");
        Require(!unavailable.IsDead && !unknown.IsDead, "Unknown health must not be reported as death.");
    }),
    ("Dead members alert by default and can be ignored explicitly", () =>
    {
        var dead = Member("a", 0);
        Require(dead.IsDead, "Available zero HP must be recognized as dead.");
        Require(Evaluate([dead], MonitorMode.Single, "a").ShouldAlert, "0% should alert by default.");
        var ignored = HealthEvaluator.Evaluate([dead], new(true, MonitorMode.Single, 50, "a", null, true));
        Require(!ignored.ShouldAlert, "Ignore-dead option must suppress dead targets.");
        var revived = HealthEvaluator.Evaluate([Member("a", 20)], new(true, MonitorMode.Single, 50, "a", null, true));
        Require(revived.ShouldAlert, "A revived low-HP member must alert again.");
    }),
    ("Disabled monitor stops alerts while retaining the selected health display", () =>
    {
        var result = HealthEvaluator.Evaluate([Member("a", 20)], new(false, MonitorMode.Single, 50, "a", null));
        Require(!result.ShouldAlert, "Disabled monitor must not alert.");
        EqualKeys(result.MonitoredMembers, "a");
    }),
    ("Leaving or disbanding the party clears the alert", () =>
    {
        Require(Evaluate([Member("a", 20)], MonitorMode.Single, "a").ShouldAlert, "Initial low HP must alert.");
        Require(!Evaluate([], MonitorMode.Single, "a").ShouldAlert, "No current member means no alert.");
        Require(!Evaluate([], MonitorMode.AllParty).ShouldAlert, "Empty all-party mode must not alert.");
    }),
    ("Changing selected target or threshold takes effect immediately", () =>
    {
        PartyMemberSnapshot[] members = [Member("a", 40), Member("b", 80)];
        Require(Evaluate(members, MonitorMode.Single, "a").ShouldAlert, "a starts below the threshold.");
        Require(!Evaluate(members, MonitorMode.Single, "b").ShouldAlert, "Switching to healthy b stops it.");
        Require(!HealthEvaluator.Evaluate(members, new(true, MonitorMode.Single, 30, "a", null)).ShouldAlert,
            "Lowering the threshold below current health stops the alarm.");
    }),
    ("Health comparison does not overflow at uint limits", () =>
    {
        var result = Evaluate([Member("a", uint.MaxValue, uint.MaxValue)], MonitorMode.Single, "a");
        Require(!result.ShouldAlert, "Full uint.MaxValue HP must remain 100%.");
        Require(Evaluate([Member("a", uint.MaxValue / 2, uint.MaxValue)], MonitorMode.Single, "a").ShouldAlert,
            "Half of uint.MaxValue must evaluate correctly.");
    }),
    ("Threshold endpoints are inclusive and invalid values are bounded", () =>
    {
        Require(HealthEvaluator.Evaluate([Member("a", 100)], new(true, MonitorMode.Single, 100, "a", null)).ShouldAlert,
            "100% threshold must include full health.");
        Require(HealthEvaluator.Evaluate([Member("a", 0)], new(true, MonitorMode.Single, 0, "a", null)).ShouldAlert,
            "0% threshold must include dead members.");
        Require(!HealthEvaluator.Evaluate([Member("a", 1)], new(true, MonitorMode.Single, -20, "a", null)).ShouldAlert,
            "Negative threshold must be bounded to zero.");
        Require(HealthEvaluator.Evaluate([Member("a", 100)], new(true, MonitorMode.Single, 200, "a", null)).ShouldAlert,
            "Threshold above 100 must be bounded to 100.");
    }),
    ("Unknown monitor mode cannot select an unintended target", () =>
    {
        var result = Evaluate([Member("a", 10)], (MonitorMode)123, "a");
        Require(!result.ShouldAlert && result.MonitoredMembers.Count == 0,
            "Invalid configuration must not broaden monitoring.");
    }),
}.Concat(RecoveryTrackerChecks.All).ToArray();

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed.");
return failed == 0 ? 0 : 1;

static PartyMemberSnapshot Member(string key, uint hp, uint maxHp = 100) => new(key, key, hp, maxHp);

static HealthEvaluation Evaluate(
    IReadOnlyList<PartyMemberSnapshot> members,
    MonitorMode mode,
    string? primary = null,
    string? secondary = null) =>
    HealthEvaluator.Evaluate(members, new(true, mode, 50, primary, secondary));

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void EqualKeys(IReadOnlyList<PartyMemberSnapshot> members, params string[] expected)
{
    var actual = members.Select(member => member.Key).ToArray();
    Require(actual.SequenceEqual(expected, StringComparer.Ordinal),
        $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
}
