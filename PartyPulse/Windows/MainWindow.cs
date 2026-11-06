using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using PartyPulse.Core;

namespace PartyPulse.Windows;

internal sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Teal = new(0.31f, 0.83f, 0.75f, 1);
    private static readonly Vector4 Red = new(1, 0.40f, 0.42f, 1);
    private static readonly Vector4 Muted = new(0.65f, 0.69f, 0.75f, 1);
    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private string soundPath;
    private string recoverySoundPath;

    internal MainWindow(Plugin plugin) : base("PartyPulse###PartyPulse.Main")
    {
        this.plugin = plugin;
        soundPath = plugin.Config.SoundPath;
        recoverySoundPath = plugin.Config.RecoverySoundPath;
        Size = new Vector2(535, 625);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(430, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var config = plugin.Config;
        ImGui.TextColored(Teal, "PARTYPULSE");
        ImGui.SameLine();
        ImGui.TextUnformatted("Watch the people who need you.");
        ImGui.Spacing();
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable health alerts", ref enabled))
        {
            config.Enabled = enabled;
            plugin.Save();
        }

        ImGui.Spacing();
        DrawMode("One person", MonitorMode.Single);
        ImGui.SameLine();
        DrawMode("Duo", MonitorMode.Duo);
        ImGui.SameLine();
        DrawMode("Entire party", MonitorMode.AllParty);

        if (config.Mode != MonitorMode.AllParty)
        {
            DrawSelection("First person", true);
            if (config.Mode == MonitorMode.Duo) DrawSelection("Second person", false);
        }
        else
        {
            WrappedMuted("Includes you and every available member of your current party.");
        }

        var threshold = config.ThresholdPercent;
        ImGui.SetNextItemWidth(-150);
        if (ImGui.SliderInt("Alert at or below", ref threshold, 0, 100, "%d%%"))
            config.ThresholdPercent = Math.Clamp(threshold, 0, 100);
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.Save();
        WrappedMuted("The sound stops when all available watched members are above this mark.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawStatus();
        DrawMembers();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Sound & preferences", ImGuiTreeNodeFlags.DefaultOpen))
            DrawPreferences();

        ImGui.Spacing();
        WrappedMuted("Closing this window keeps monitoring active. /partypulse to reopen.");
    }

    private void DrawMode(string label, MonitorMode mode)
    {
        if (!ImGui.RadioButton(label, plugin.Config.Mode == mode)) return;
        plugin.Config.Mode = mode;
        if (mode == MonitorMode.Duo && plugin.Config.PrimaryKey == plugin.Config.SecondaryKey)
        {
            plugin.Config.SecondaryKey = null;
            plugin.Config.SecondaryName = null;
        }
        plugin.Save();
    }

    private void DrawSelection(string label, bool primary)
    {
        var config = plugin.Config;
        var key = primary ? config.PrimaryKey : config.SecondaryKey;
        var savedName = primary ? config.PrimaryName : config.SecondaryName;
        var otherKey = primary ? config.SecondaryKey : config.PrimaryKey;
        var member = plugin.Members.FirstOrDefault(m => m.Key == key);
        var preview = member?.Name ?? (key == null ? "Choose a party member..." : $"{savedName ?? "Selected member"} (not in party)");
        ImGui.SetNextItemWidth(-115);
        if (!ImGui.BeginCombo(label, preview)) return;
        if (ImGui.Selectable("None", key == null)) SetSelection(primary, null);
        foreach (var candidate in plugin.Members)
        {
            var duplicate = config.Mode == MonitorMode.Duo && candidate.Key == otherKey;
            ImGui.BeginDisabled(duplicate);
            if (ImGui.Selectable($"{candidate.Name}##{candidate.Key}", candidate.Key == key))
                SetSelection(primary, candidate);
            ImGui.EndDisabled();
        }
        ImGui.EndCombo();
    }

    private void SetSelection(bool primary, PartyMemberSnapshot? member)
    {
        if (primary)
        {
            plugin.Config.PrimaryKey = member?.Key;
            plugin.Config.PrimaryName = member?.Name;
        }
        else
        {
            plugin.Config.SecondaryKey = member?.Key;
            plugin.Config.SecondaryName = member?.Name;
        }
        plugin.Save();
    }

    private void DrawStatus()
    {
        if (!plugin.Config.Enabled)
            ImGui.TextColored(Muted, "ALERTS PAUSED");
        else if (plugin.PauseReason != null)
            ImGui.TextWrapped(plugin.PauseReason);
        else if (plugin.Evaluation.ShouldAlert)
            ImGui.TextColored(Red, $"LOW HEALTH - {plugin.Evaluation.LowMembers.Count} watched member(s)");
        else if (plugin.Evaluation.MonitoredMembers.Count == 0)
            ImGui.TextWrapped("Choose a party member above to start watching their health.");
        else if (plugin.Evaluation.MonitoredMembers.All(m => m.HpPercent == null))
            ImGui.TextColored(Muted, "WAITING FOR LIVE HP");
        else
            ImGui.TextColored(Teal, "WATCHING - no active health alert");

        if (plugin.Config.Mode != MonitorMode.AllParty)
        {
            DrawMissingSelection(plugin.Config.PrimaryKey, plugin.Config.PrimaryName, "First person");
            if (plugin.Config.Mode == MonitorMode.Duo)
                DrawMissingSelection(plugin.Config.SecondaryKey, plugin.Config.SecondaryName, "Second person");
        }
        if (plugin.Audio.Error != null)
            ImGui.TextWrapped($"Sound unavailable: {plugin.Audio.Error} Use Apply sound / retry after fixing the file or device.");
        else if (plugin.Audio.IsPreviewing)
            ImGui.TextColored(Teal, "Playing a five-second sound preview.");
        else if (plugin.Audio.IsPlaying)
            ImGui.TextColored(Red, "Sound alert is looping.");
    }

    private void DrawMissingSelection(string? key, string? name, string label)
    {
        if (key == null)
            ImGui.TextDisabled($"{label}: no selection.");
        else if (plugin.PauseReason == null && plugin.Members.All(m => m.Key != key))
            ImGui.TextWrapped($"Waiting for {name ?? "selected member"} to rejoin the party.");
    }

    private void DrawMembers()
    {
        foreach (var member in plugin.Evaluation.MonitoredMembers)
        {
            ImGui.PushID(member.Key);
            ImGui.Spacing();
            ImGui.TextUnformatted(member.Name);
            if (member.HpPercent is not { } percent)
            {
                WrappedMuted("HP unavailable - other area, disconnected, or out of view.");
            }
            else
            {
                var low = plugin.Evaluation.LowMembers.Any(m => m.Key == member.Key);
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, low ? Red : Teal);
                ImGui.ProgressBar(Math.Clamp((float)(percent / 100), 0, 1), new Vector2(-1, 21),
                    $"{percent:0.0}%   |   {member.CurrentHp:N0} / {member.MaxHp:N0} HP");
                ImGui.PopStyleColor();
            }
            ImGui.PopID();
        }
    }

    private void DrawPreferences()
    {
        var config = plugin.Config;
        var isDefault = string.IsNullOrWhiteSpace(config.SoundPath);
        var soundName = isDefault ? Plugin.DefaultSoundName : $"Custom: {Path.GetFileName(config.SoundPath)}";
        ImGui.SetNextItemWidth(-110);
        if (ImGui.BeginCombo("Alert sound", soundName))
        {
            if (ImGui.Selectable(Plugin.DefaultSoundName, isDefault))
            {
                soundPath = string.Empty;
                ApplySound();
            }
            if (!isDefault) ImGui.Selectable(soundName, true);
            if (ImGui.Selectable("Choose custom sound file...")) ChooseCustomSound();
            ImGui.EndCombo();
        }
        if (ImGui.Button("Choose custom sound...")) ChooseCustomSound();
        WrappedMuted("Choose a WAV or MP3 from your computer. Select Pokemon Low Sound Warning above to restore the default.");

        if (!isDefault)
        {
            ImGui.SetNextItemWidth(-110);
            ImGui.InputText("Custom file", ref soundPath, 2048);
        }
        if (ImGui.Button("Apply sound / retry")) ApplySound();
        ImGui.SameLine();
        if (plugin.Audio.IsPreviewing)
        {
            if (ImGui.Button("Stop preview")) plugin.Audio.StopPreview();
        }
        else if (ImGui.Button("Test sound (5s)"))
        {
            ApplySound();
            plugin.Audio.Preview();
        }
        if (!isDefault) WrappedMuted("Typed file paths take effect when applied.");

        var volume = config.Volume * 100;
        ImGui.SetNextItemWidth(-150);
        if (ImGui.SliderFloat("Alert volume", ref volume, 0, 100, "%.0f%%"))
        {
            plugin.SetVolume(volume / 100);
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveVolume();
        WrappedMuted("Only changes this alert's volume, independently of game audio. 0% mutes; 100% uses the file's full volume. Adjustments apply live.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var recoveryEnabled = config.RecoveryEnabled;
        if (ImGui.Checkbox("Enable recovery sound", ref recoveryEnabled))
        {
            config.RecoveryEnabled = recoveryEnabled;
            plugin.Save(updateRecoveryAudio: recoveryEnabled);
        }
        WrappedMuted("Optional, off by default. Plays once when a watched person goes from at or below the HP threshold to above it.");
        if (config.RecoveryEnabled) DrawRecoveryPreferences();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var ignoreDead = config.IgnoreDead;
        if (ImGui.Checkbox("Ignore KO'd members (0 HP)", ref ignoreDead))
        {
            config.IgnoreDead = ignoreDead;
            plugin.Save();
        }
        var open = config.OpenOnLoad;
        if (ImGui.Checkbox("Open this window when the plugin loads", ref open))
        {
            config.OpenOnLoad = open;
            plugin.Save();
        }
        ImGui.TextWrapped("Only party members with a live, loaded character in your area can trigger sound. Unavailable members are skipped until their HP can be read again.");
    }

    private void DrawRecoveryPreferences()
    {
        ImGui.PushID("RecoverySound");
        var config = plugin.Config;
        var isDefault = string.IsNullOrWhiteSpace(config.RecoverySoundPath);
        var name = isDefault ? Plugin.DefaultRecoverySoundName : $"Custom: {Path.GetFileName(config.RecoverySoundPath)}";
        ImGui.SetNextItemWidth(-140);
        if (ImGui.BeginCombo("Recovery sound", name))
        {
            if (ImGui.Selectable(Plugin.DefaultRecoverySoundName, isDefault))
            {
                recoverySoundPath = string.Empty;
                ApplyRecoverySound();
            }
            if (!isDefault) ImGui.Selectable(name, true);
            if (ImGui.Selectable("Choose custom recovery sound...")) ChooseCustomRecoverySound();
            ImGui.EndCombo();
        }
        if (ImGui.Button("Choose custom recovery sound...")) ChooseCustomRecoverySound();
        if (!isDefault)
        {
            ImGui.SetNextItemWidth(-110);
            ImGui.InputText("Custom file", ref recoverySoundPath, 2048);
        }
        if (ImGui.Button("Apply / retry recovery sound")) ApplyRecoverySound();
        ImGui.BeginDisabled(!config.Enabled);
        if (plugin.RecoveryAudio.IsPlaying)
        {
            if (ImGui.Button("Stop recovery sound")) plugin.RecoveryAudio.Stop();
        }
        else if (ImGui.Button("Test recovery sound once"))
        {
            ApplyRecoverySound();
            plugin.RecoveryAudio.PlayOnce();
        }
        ImGui.EndDisabled();
        if (!config.Enabled) WrappedMuted("Enable health alerts above to test the recovery sound.");

        var volume = config.RecoveryVolume * 100;
        ImGui.SetNextItemWidth(-150);
        if (ImGui.SliderFloat("Recovery volume", ref volume, 0, 100, "%.0f%%"))
            plugin.SetRecoveryVolume(volume / 100);
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveVolume();
        WrappedMuted("Separate volume for the recovery sound. Custom WAV/MP3 files are supported. The sound plays to the end once, without looping.");
        if (config.Mode != MonitorMode.Single)
            WrappedMuted("Each watched member can trigger recovery, even if another is still low. Simultaneous recoveries play one sound.");
        if (plugin.RecoveryAudio.Error != null)
            ImGui.TextWrapped($"Recovery sound unavailable: {plugin.RecoveryAudio.Error} Use Apply / retry recovery sound after fixing the file or device.");
        ImGui.PopID();
    }

    private void ApplySound()
    {
        plugin.Config.SoundPath = soundPath.Trim().Trim('"');
        soundPath = plugin.Config.SoundPath;
        plugin.Save(updateAudio: true);
    }

    private void ChooseCustomSound()
    {
        fileDialog.OpenFileDialog("Choose a custom alert sound", "Audio files{.wav,.mp3},.*", (success, path) =>
        {
            if (!success) return;
            soundPath = path;
            ApplySound();
        });
    }

    private void ApplyRecoverySound()
    {
        plugin.Config.RecoverySoundPath = recoverySoundPath.Trim().Trim('"');
        recoverySoundPath = plugin.Config.RecoverySoundPath;
        plugin.Save(updateRecoveryAudio: true);
    }

    private void ChooseCustomRecoverySound()
    {
        fileDialog.OpenFileDialog("Choose a custom recovery sound", "Audio files{.wav,.mp3},.*", (success, path) =>
        {
            if (!success) return;
            recoverySoundPath = path;
            ApplyRecoverySound();
        });
    }

    private static void WrappedMuted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    internal void DrawFileDialog() => fileDialog.Draw();
    public void Dispose() => fileDialog.Reset();
}
