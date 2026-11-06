# PartyPulse

A Windows Dalamud/XIVLauncher plugin that watches party HP and loops a sound while someone you are watching needs attention.

## Use it

1. Open `/xlsettings` in FFXIV and go to **Experimental > Custom Plugin Repositories**.
2. Add this repository URL, enable it, and save:

   ```text
   https://raw.githubusercontent.com/unmonitoredlemmings/PartyPulse/main/repo.json
   ```

3. Open `/xlplugins`, search for **PartyPulse** under available plugins, and install it. If you previously loaded a development copy, disable that copy and remove its Dev Plugin Location before installing through this repository.
4. Open `/partypulse`. Choose **One person**, **Duo**, or **Entire party**, then select the people to watch from your current party.
5. Set **Alert at or below** to the desired HP percentage. Under **Sound & preferences**, the default **Alert sound** is **Pokemon Low Sound Warning**. Use **Choose custom sound...** to select your own WAV/MP3 file, adjust **Alert volume**, and use **Test sound (5s)**.
6. Optionally turn on **Enable recovery sound** in the same menu. It starts **off**, including when upgrading. The bundled recovery sound is **Uwu Hannah**; choose a custom recovery WAV/MP3 and adjust **Recovery volume** independently if desired.

PartyPulse uses a custom repository; it is not part of Dalamud's official default list. The public feed and package URLs follow [Dalamud's custom repository format](https://dalamud.dev/plugin-publishing/custom-repositories/). The feed supports installation and updates through the normal plugin installer.

For local development, build the project (or extract the packaged ZIP to a permanent folder), add the full path to `PartyPulse.dll` in **Experimental > Dev Plugin Locations**, then enable it under **Dev Tools > Installed Dev Plugins**. Keep the DLLs and `Assets` folder together. See the [sample plugin's local loading instructions](https://github.com/goatcorp/SamplePlugin#activating-in-game).

## Alert behavior

- Default threshold: **50%**. Exactly 50% triggers a 50% alert; 50.01% does not. The displayed percentage is rounded, but the check uses exact HP values.
- **One person:** watch one selected party member.
- **Duo:** watch two different selected members. Either person can start the sound; both must recover for it to stop.
- **Entire party:** watch every available member of your current party, including yourself. Any member can start the sound; every available watched member must recover for it to stop. This does not include other alliance parties.
- One sound loops, even if several people are low. It is not restarted on every health update. Monitoring runs roughly every 100 ms.
- Closing the window keeps monitoring active. `/partypulse off` pauses alerts immediately; `/partypulse on` resumes them.
- Selections are remembered by character identity, not party position. A member leaving does not redirect the alert to someone else. Missing selections remain waiting until that character returns or you change the selection.
- Zero HP triggers by default. **Ignore KO'd members (0 HP)** excludes those members if enabled.
- The threshold can be set from 0–100%. At 0%, only zero HP qualifies; at 100%, even full HP qualifies.

**Availability limit:** Only members with a valid, loaded character in your current area have actionable health readings. Other-area, disconnected, and out-of-view members are shown as unavailable and skipped. The alert also stops if a low member becomes unavailable or leaves. This avoids endlessly sounding from stale party-list HP. Monitoring and previews stop while logged out or changing areas.

**Default sound:** **Pokemon Low Sound Warning** uses the supplied `low-hp-pokemon.mp3`, bundled inside the plugin's `Assets` folder. No Downloads-folder path is required. Select it in the **Alert sound** menu to switch back from a custom sound. Existing custom sound selections are preserved when upgrading.

**Custom sound:** **Choose custom sound...** opens a local file picker for WAV/MP3 files and applies the chosen file. No online upload is needed. You can also edit a custom file's path and use **Apply sound / retry**. Missing/corrupt files or device errors appear in the window; use that button to retry after fixing the problem.

**Independent volume:** **Alert volume** controls only PartyPulse's sound effect, separately from FFXIV's music, effects, and master-volume settings. The range is 0–100%: 0% mutes the alert, and 100% plays at the file's full volume. The default is 50%. Changes apply immediately while a warning or preview plays and are saved on slider release. This software gain does not change the game's audio settings or the Windows device volume; Windows system/mixer mute and device volume still apply.

PCM WAV and supported MP3 files play through Windows' default output device. Changing Windows' output device may require a plugin reload. Sound previews last five seconds, and a real low-health alert takes priority.

Settings, selections, and volume are saved through Dalamud's normal plugin configuration storage. Custom sound files stay at the path you choose; they are not copied into the package.

## Optional recovery sound

The recovery sound is disabled by default. Once enabled, it plays the full selected sound **once** when a watched person's valid HP moves from **at or below** your configured threshold to **strictly above** it. For example, with a 50% threshold, 49% → 50% does not qualify, but 50% → 50.01% does. Remaining healthy does not repeat the sound; a new low-health episode can trigger a new recovery.

- The default is **Uwu Hannah**, using the supplied `uwu-hannah.mp3` bundled in `Assets`. The regular low-health warning remains **Pokemon Low Sound Warning**.
- **Choose custom recovery sound...** selects a local WAV/MP3. The **Recovery sound** menu switches back to the bundled default.
- **Recovery volume** ranges from 0–100%, defaults to 50%, and changes live without changing the warning volume or game audio. **Test recovery sound once** previews the complete sound without looping.
- In duo and entire-party modes, each watched member can recover independently, even while someone else still needs the looping low-health warning. Several recoveries in the same sample play one sound. A new recovery during playback restarts the recovery sound from the beginning instead of stacking multiple copies.
- Loading the plugin, enabling recovery while someone is already healthy, editing the threshold/selection/mode, leaving the party, and becoming unavailable do **not** count as healing. The tracker first observes a valid low-health sample before a later valid healthy sample can trigger recovery. Changing settings or losing valid HP starts a new baseline.
- Disabling recovery, pausing all alerts, logging out, changing areas, or unloading the plugin stops recovery playback. The feature respects **Ignore KO'd members**.

## Build and verify

Requires Windows, a **.NET 10 SDK**, and an installed **Dalamud API 15** development directory. This project was compiled against the local Dalamud **15.0.3.9** installation using `Dalamud.NET.Sdk/15.0.0`, matching the [current SDK sample](https://raw.githubusercontent.com/goatcorp/SamplePlugin/master/SamplePlugin/SamplePlugin.csproj).

Run from this project folder:

```powershell
.\build.ps1
```

The script builds Release, runs the health and audio checks (including decoding both bundled MP3s, checking volume scaling, and recovery transitions) without playing sound, and writes `dist/PartyPulse-1.0.2.zip`. It uses a workspace-local SDK if one exists at `../.tools/dotnet`; otherwise it uses `dotnet` on PATH. Build caches and temporary files stay local, and environment overrides are restored afterward. Set `DALAMUD_HOME` before building if Dalamud is installed elsewhere.

You can also use standard commands with your usual development environment:

```powershell
dotnet build PartyPulse.slnx -c Release
dotnet run --project PartyPulse.Tests -c Release
dotnet run --project PartyPulse.AudioTests -c Release -- .\PartyPulse\Assets\low-hp-pokemon.mp3 .\PartyPulse\Assets\uwu-hannah.mp3
```

Tests cover inclusive thresholds, duo recovery, full-party selection, missing members, party reordering, unavailable HP, KO handling, numeric overflow, recovery transitions without false positives, audio looping, one-time playback, empty/corrupt files, disposal, MP3 decoding, and independent volume scaling. They do not require the game or play audio.

**Live validation remains:** Load in-game, select a party member, test both sounds, and confirm an HP dip starts the warning loop and recovery stops it. Verify that recovery stays silent until enabled, then plays exactly once on healing above the threshold. Repeat in duo/party mode, then check party departure, area changes, pause, and plugin unload. Compilation and automated checks do not replace this in-game test.

## Source layout

- `PartyPulse`: Dalamud adapter, settings window, sound players, bundled warning and recovery MP3s.
- `PartyPulse.Core`: game-independent selection and HP threshold evaluation.
- `PartyPulse.Tests`: executable health checks with no test-framework dependency.
- `PartyPulse.AudioTests`: executable audio stream/error checks; never starts device playback.

Audio playback uses [NAudio](https://github.com/naudio/NAudio), under its MIT license. The bundled sounds are the user-provided `low-hp-pokemon.mp3` and `uwu-hannah.mp3`.

## Publishing an update

1. Increment the project version, update the package filename in `build.ps1`, and build/test with `./build.ps1`.
2. Run `./scripts/Prepare-Repository.ps1` to validate the built package, copy it to `releases`, and regenerate `repo.json` from the exact manifest inside the ZIP.
3. Commit the new versioned ZIP and `repo.json` together, along with the updated source. Push to the public repository's `main` branch. Existing users keep the same repository URL.

The feed and ZIP must be publicly downloadable without authentication. GitHub Pages and a separate GitHub Release are not required for this layout. The publishing preparation script does not upload anything or change the installed SDK.
