using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PartyPulse.Audio;
using PartyPulse.Core;
using PartyPulse.Windows;

namespace PartyPulse;

public sealed class Plugin : IDalamudPlugin
{
    internal const string DefaultSoundName = "Pokemon Low Sound Warning";
    internal const string DefaultRecoverySoundName = "Uwu Hannah";
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static ICondition Conditions { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windows = new("PartyPulse");
    private readonly MainWindow mainWindow;
    private readonly RecoveryTracker recoveryTracker = new();
    private long lastSample;
    private bool snapshotFailed;
    private bool disposed;
    private int resetRecoveryHistory;

    internal Configuration Config { get; }
    internal AlertAudio Audio { get; } = new();
    internal OneShotAudio RecoveryAudio { get; } = new();
    internal IReadOnlyList<PartyMemberSnapshot> Members { get; private set; } = Array.Empty<PartyMemberSnapshot>();
    internal HealthEvaluation Evaluation { get; private set; } = new(Array.Empty<PartyMemberSnapshot>(), Array.Empty<PartyMemberSnapshot>());
    internal string? PauseReason { get; private set; } = "Waiting for party data.";
    internal string DefaultSoundPath { get; }
    internal string DefaultRecoverySoundPath { get; }

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Normalize();
        DefaultSoundPath = Path.Combine(PluginInterface.AssemblyLocation.Directory!.FullName, "Assets", "low-hp-pokemon.mp3");
        DefaultRecoverySoundPath = Path.Combine(PluginInterface.AssemblyLocation.Directory!.FullName, "Assets", "uwu-hannah.mp3");
        ConfigureAudio();
        if (Config.RecoveryEnabled) ConfigureRecoveryAudio();
        mainWindow = new MainWindow(this) { IsOpen = Config.OpenOnLoad };
        windows.AddWindow(mainWindow);
        Commands.AddHandler("/partypulse", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open PartyPulse. /partypulse on or off enables or pauses health alerts.",
        });
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
        Framework.Update += Update;
    }

    internal void Save(bool updateAudio = false, bool updateRecoveryAudio = false)
    {
        Config.Normalize();
        PluginInterface.SavePluginConfig(Config);
        if (updateAudio) ConfigureAudio();
        if (updateRecoveryAudio) ConfigureRecoveryAudio();
        if (!Config.Enabled) Audio.Stop();
        if (!Config.Enabled || !Config.RecoveryEnabled)
        {
            RecoveryAudio.Stop();
            // Only the sampling thread mutates transition history.
            Interlocked.Exchange(ref resetRecoveryHistory, 1);
        }
        RefreshEvaluation();
    }

    private void ConfigureAudio() => Audio.Configure(
        string.IsNullOrWhiteSpace(Config.SoundPath) ? DefaultSoundPath : Config.SoundPath,
        Config.Volume);

    private void ConfigureRecoveryAudio() => RecoveryAudio.Configure(
        string.IsNullOrWhiteSpace(Config.RecoverySoundPath) ? DefaultRecoverySoundPath : Config.RecoverySoundPath,
        Config.RecoveryVolume);

    internal void SetVolume(float volume)
    {
        Config.Volume = float.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0.5f;
        Audio.SetVolume(Config.Volume);
    }

    internal void SetRecoveryVolume(float volume)
    {
        Config.RecoveryVolume = float.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0.5f;
        RecoveryAudio.SetVolume(Config.RecoveryVolume);
    }

    // Persist on slider release without reloading the file or interrupting a preview.
    internal void SaveVolume() => PluginInterface.SavePluginConfig(Config);

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on": Config.Enabled = true; Save(); break;
            case "off": Config.Enabled = false; Save(); break;
            default: mainWindow.Toggle(); break;
        }
    }

    private void Open() => mainWindow.IsOpen = true;
    private void Draw()
    {
        windows.Draw();
        mainWindow.DrawFileDialog();
    }

    private void Update(IFramework framework)
    {
        if (disposed) return;
        try
        {
            if (!ClientState.IsLoggedIn || Objects.LocalPlayer == null)
            {
                Pause("Log in to monitor your party.");
                return;
            }
            if (Conditions[ConditionFlag.BetweenAreas] || Conditions[ConditionFlag.BetweenAreas51] || Conditions[ConditionFlag.LoggingOut])
            {
                Pause("Monitoring paused while changing areas.");
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(lastSample, now).TotalMilliseconds >= 100)
            {
                lastSample = now;
                ReadParty();
                snapshotFailed = false;
            }
            Audio.SetAlert(Config.Enabled && Evaluation.ShouldAlert);
        }
        catch (Exception ex)
        {
            Pause("Party data could not be read. Waiting for a fresh update.");
            if (!snapshotFailed) Log.Error(ex, "Could not update PartyPulse health monitoring.");
            snapshotFailed = true;
        }
    }

    private void ReadParty()
    {
        var members = new List<PartyMemberSnapshot>(8);
        foreach (var member in PartyList)
        {
            var key = member.ContentId != 0
                ? $"cid:{member.ContentId}"
                : $"name:{member.Name.TextValue}@{member.World.RowId}";
            var world = member.World.ValueNullable?.Name.ToString();
            var name = string.IsNullOrEmpty(world) ? member.Name.TextValue : $"{member.Name.TextValue} @ {world}";
            // Use a live character to avoid stale/offline party-list HP triggering an endless alarm.
            // Unloaded or other-zone members remain visible but have no actionable HP reading.
            var actor = member.GameObject as ICharacter;
            var available = member.Territory.RowId == ClientState.TerritoryType &&
                            actor != null && actor.IsValid() && actor.MaxHp > 0;
            members.Add(new PartyMemberSnapshot(key, name,
                available ? actor!.CurrentHp : 0,
                available ? actor!.MaxHp : 0,
                available));
        }
        Members = members;
        PauseReason = members.Count == 0 ? "Join a party to choose members to monitor." : null;
        RefreshEvaluation(freshSnapshot: true);
    }

    private void RefreshEvaluation(bool freshSnapshot = false)
    {
        Evaluation = HealthEvaluator.Evaluate(Members, Config.ToSettings());
        Audio.SetAlert(Config.Enabled && PauseReason == null && Evaluation.ShouldAlert);
        // UI/configuration updates may re-evaluate warnings but must never manufacture a recovery.
        if (!freshSnapshot) return;
        if (Interlocked.Exchange(ref resetRecoveryHistory, 0) != 0) recoveryTracker.Reset();
        var recovered = recoveryTracker.Update(Members, Config.ToSettings(), Config.RecoveryEnabled);
        if (PauseReason == null && recovered.Count > 0)
            RecoveryAudio.PlayOnce();
    }

    private void Pause(string reason)
    {
        PauseReason = reason;
        Members = Array.Empty<PartyMemberSnapshot>();
        Evaluation = HealthEvaluator.Evaluate(Members, Config.ToSettings());
        Audio.Stop();
        RecoveryAudio.Stop();
        recoveryTracker.Reset();
        lastSample = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Framework.Update -= Update;
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        Commands.RemoveHandler("/partypulse");
        windows.RemoveAllWindows();
        mainWindow.Dispose();
        Audio.Dispose();
        RecoveryAudio.Dispose();
        // Persist a slider edit even if the window was closed before ImGui reported deactivation.
        try { PluginInterface.SavePluginConfig(Config); }
        catch (Exception ex) { Log.Error(ex, "Could not save PartyPulse settings on unload."); }
    }
}
