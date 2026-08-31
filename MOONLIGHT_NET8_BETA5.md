# Moonlight .NET 8 Beta 5 — Developer Handoff

## Purpose

This document summarizes the remote-play work added on the `moonlight-net8-beta5` branch of TeknoParrotUI.

The branch builds on the .NET 8 / Avalonia migration and focuses on bringing the existing Sunshine/Moonlight remote-play functionality into the modern UI and input architecture without carrying forward the old WPF implementation directly.

It is intended as a handoff for developers and testers who need to understand:

- what changed
- how Sunshine and Moonlight are expected to be installed
- how remote input is routed
- how the new UI behaves
- which game profiles were updated
- what has already been tested
- what should still be tested before wider release

---

## Branch

```text
moonlight-net8-beta5
```

Current remote-play/profile fix commit:

```text
d5397e25 Fix remote local play controls and IT profiles
```

Important preceding branch commits include:

```text
439da92d Label remote play beta
cc5e7e55 Promote remote play setup to main navigation
bfcded86 Clean up remote play UI text and Rod setup layout
2198dbb4 Add remote local play mappings to IT game profiles
50629c65 Restore Rod preferred setup and profile migration behavior
aa2ac99d Port Moonlight and Sunshine management UI to Avalonia
a77a4b4e Port Sunshine remote input plumbing to .NET 8
```

---

# 1. Remote Play Architecture

## Sunshine = Host

Sunshine runs on the PC hosting the TeknoParrot game.

TeknoParrotUI manages the Sunshine process and integrates with the custom Sunshine input pipe used for TeknoParrot remote-play input.

## Moonlight = Client

Moonlight runs on the remote client device.

The client connects to the Sunshine host and sends controller/input data through the normal Moonlight/Sunshine streaming session.

## TeknoParrotUI = Orchestrator

TeknoParrotUI is responsible for:

- detecting Sunshine and Moonlight
- launching/managing the appropriate application
- configuring the remote-play mode
- receiving Sunshine input
- translating remote player inputs into TeknoParrot mappings
- exposing remote-player controls in the Avalonia UI
- routing remote trackball input independently for multiple players

The long-term architecture may add authenticated host discovery through TeknoParrot services so users can select hosts by identity rather than manually exchanging addresses. That discovery layer is not part of this beta.

---

# 2. Sunshine / Moonlight Installation Layout

Sunshine and Moonlight are **not bundled inside the TeknoParrot distribution**.

Users or testers download the custom portable Sunshine and Moonlight packages separately and place them in root-level folders beside `TeknoParrotUi.exe`.

Expected layout:

```text
TeknoParrot├── TeknoParrotUi.exe
├── Sunshine│   └── ...
├── Moonlight│   └── ...
└── ...
```

TeknoParrotUI should detect and manage these folders rather than depending on machine-wide Sunshine or Moonlight installations.

---

# 3. Remote Play UI

Remote Play is now a first-class navigation item in the Avalonia sidebar rather than a subsection buried inside Settings.

The page allows the user to work with:

- Sunshine host mode
- Moonlight client mode
- process state
- setup/status information

Current host status wording:

```text
Sunshine is running in TeknoParrot managed mode.
```

Current page description:

```text
Set up remote play for TeknoParrot games using Sunshine host or Moonlight client.
```

The current beta build title identifies the remote-play test build:

```text
TeknoParrot UI - BETA 5 - Remote Play .NET 8
```

---

# 4. Remote Local Play Setting

The old three-state behavior has been simplified.

## Current behavior

```text
Remote Local Play
- Off
- On
```

The old:

```text
Host Only
```

mode has been removed from active UI/runtime behavior.

Legacy XML/model compatibility fields may remain in the codebase where necessary for old profiles, but current IT profiles and runtime logic no longer depend on Host Only.

---

# 5. Input API Behavior

Remote-play mode now controls the Input API automatically.

## Remote Local Play = Off

The Input API selector is enabled.

Available choices are:

```text
RawInput
RawInputTrackball
```

The user may select the normal local input mode.

## Remote Local Play = On

TeknoParrotUI automatically switches to:

```text
MergedInput
```

The Input API selector is disabled while remote play is active.

This allows TeknoParrot to combine local and Sunshine-provided input through the unified input path.

## Switching back Off

When Remote Local Play is disabled:

- the Input API selector becomes enabled again
- if the current value is `MergedInput`, TeknoParrotUI falls back to `RawInputTrackball`

---

# 6. Sunshine Input Integration

The .NET 8 port includes the Sunshine-side input bridge required by remote play.

Key concepts include:

- Sunshine player identity
- active capture source tracking
- Sunshine input listener
- mapping dispatch integration
- remote player digital-button state
- trackball shared-memory routing
- virtual controller support

Relevant runtime areas include:

```text
TeknoParrotUi.Common/InputListening/SunshineInputListener.cs
TeknoParrotUi.Common/InputListening/InputListenersManager.cs
TeknoParrotUi.Common/MappingDispatch.cs
TeknoParrotUi.Common/JoystickMapping.cs
TeknoParrotUi.Common/Pipes/
```

The Sunshine integration uses the TeknoParrot-specific pipe:

```text
SunshineTeknoParrotInput
```

---

# 7. Multi-Player Input Model

Remote Local Play supports separate player mappings for:

```text
P1
P2
P3
P4
```

The control UI now uses player-oriented labels rather than Host-oriented labels.

For example:

```text
P1 Start
P1 Left
P1 Right
P1 FlyBy
P1 Spin
P1 Option
P1 Help
P1 Switch Club Left
P1 Switch Club Right

P2 ...
P3 ...
P4 ...
```

This naming works correctly whether remote play is enabled or disabled.

The underlying `InputMapping` values remain unchanged.

---

# 8. Trackball Routing

Trackball input is handled separately for each remote player.

Current supported routing includes:

```text
P1 / local trackball
P2 trackball
P3 trackball
P4 trackball
Host remote trackball channel
```

Remote trackball data is routed through separate memory-mapped channels rather than being merged into one global trackball state.

This is important for games such as Golden Tee, PowerPutt, Silver Strike, and other Incredible Technologies titles.

---

# 9. Per-Player Trackball Sensitivity

Affected IT profiles now expose per-player remote trackball sensitivity controls where appropriate.

Examples:

```text
P2 Trackball Sensitivity X
P2 Trackball Sensitivity Y

P3 Trackball Sensitivity X
P3 Trackball Sensitivity Y

P4 Trackball Sensitivity X
P4 Trackball Sensitivity Y
```

These settings appear when Remote Local Play is enabled.

Base/local sensitivity settings remain available separately.

Runtime consumption of every per-player sensitivity field should continue to be verified during testing.

---

# 10. Controls UI Changes

The Avalonia control-binding UI was updated to understand the new Remote Local Play behavior.

Relevant areas:

```text
TeknoParrotUi.Avalonia/Views/JoystickSetupView.axaml.cs
TeknoParrotUi.Avalonia/Views/MultiButtonConfigView.axaml.cs
TeknoParrotUi.Avalonia/Views/GameSettingsView.axaml.cs
```

Changes include:

- removing Host Only-specific visibility behavior
- using Remote Local Play mode for visibility decisions
- exposing remote player bindings
- allowing `MergedInput` to participate in remote-play capture
- keeping normal local input behavior intact when Remote Local Play is Off

---

# 11. Incredible Technologies Profile Updates

The following profiles were updated for the new remote-play structure:

```text
GoldenTeeLive2006.xml
GoldenTeeLive2007.xml
GoldenTeeLive2008.xml
GoldenTeeLive2009.xml
GoldenTeeLive2010.xml
GoldenTeeLive2011.xml
GoldenTeeLive2012.xml
GoldenTeeLive2013.xml
GoldenTeeLive2014.xml
GoldenTeeLive2015.xml
GoldenTeeLive2016.xml
GoldenTeeLive2017.xml
GoldenTeeLive2018.xml
GoldenTeeLive2019.xml
PowerPuttLive2012.xml
PowerPuttLive2013.xml
PuckOff.xml
SilverStrikeBowlingLive.xml
TargetTossProBags.xml
TargetTossProLawndarts.xml
```

The profile changes include:

- Remote Local Play reduced to Off / On
- legacy Host Only visibility flags removed
- remote digital mappings placed inside the correct `JoystickButtons` collection
- player controls organized as P1 / P2 / P3 / P4
- trackball mappings retained separately
- per-player sensitivity controls added where applicable
- profile revisions incremented so existing user profiles migrate to the new stock structure

---

# 12. GameProfileRevision Requirement

Whenever the structure of a stock game profile changes, its:

```xml
<GameProfileRevision>
```

must also be incremented.

If the revision is not changed, existing users may continue loading the old `UserProfiles` copy and never see:

- new controls
- renamed controls
- reordered controls
- new settings
- new visibility behavior

This was confirmed during Golden Tee 2019 testing: changing the stock XML alone did not update the UI until the profile revision was increased.

---

# 13. Golden Tee 2019 Validation

Golden Tee Live 2019 was the primary real-world validation profile during development.

Testing confirmed:

- Sunshine/Moonlight streaming works
- game launches and is playable remotely
- remote P1 digital input works
- remote P2 trackball routing works
- missing P2/P3/P4 controls were traced to malformed XML structure rather than the Sunshine transport
- reorganizing the `JoystickButtons` collection fixed the control visibility problem
- increasing `GameProfileRevision` forced the corrected structure into the user profile
- Remote Local Play On correctly forces `MergedInput`
- Remote Local Play Off restores the local input choices
- player-oriented P1/P2/P3/P4 naming is clearer than Host/P2/P3/P4 naming

This profile became the template for the other IT profile fixes.

---

# 14. Rod Preferred Setup Port

This branch also contains the .NET 8/Avalonia port of the Rod preferred-setup behavior.

The current modes are independent and mutually exclusive:

## Default

Neither option selected.

- baseline defaults
- no sensitivity locks

## Rod's Preferred Setup

- applies Rod-preferred values
- locks the appropriate sensitivity settings

## Custom / Override Default Outfit

- enables user customization
- does not force Rod-preferred values

Rod's Preferred Setup and Custom cannot both be enabled.

Migration behavior was also restored so older saved profiles can move forward to the newer settings structure.

---

# 15. Packaging / Publish

The Windows release is generated using:

```powershell
.\publish.ps1 -Zip
```

The publish process builds the Avalonia application and ParrotPatcher, creates the portable output, moves supporting dependencies into the expected layout, and generates a ZIP suitable for tester distribution.

For remote-play testing, the tester must still separately provide:

```text
SunshineMoonlight```

beside the published `TeknoParrotUi.exe`.

---

# 16. Recommended Test Flow

## Basic UI

- launch TeknoParrotUI
- confirm Remote Play appears in the main navigation
- confirm the page opens correctly
- verify Sunshine and Moonlight detection

## Game Settings

With an affected IT title:

1. Open Game Settings.
2. Confirm Remote Local Play contains only:
   - Off
   - On
3. Set Remote Local Play to Off.
4. Confirm Input API allows:
   - RawInput
   - RawInputTrackball
5. Set Remote Local Play to On.
6. Confirm Input API changes to `MergedInput`.
7. Confirm the Input API selector becomes disabled.
8. Turn Remote Local Play Off again.
9. Confirm the selector becomes enabled again.

## Controls

With Remote Local Play enabled:

- confirm P1 controls appear
- confirm P2 controls appear
- confirm P3 controls appear
- confirm P4 controls appear
- confirm trackball mappings appear where expected
- confirm there are no duplicate control names

## Runtime

Test at minimum:

- local P1 controls
- remote P1 controls
- remote P2 digital controls
- remote P2 trackball
- P3 digital + trackball if hardware/client setup permits
- P4 digital + trackball if hardware/client setup permits
- local controls with Remote Local Play disabled

---

# 17. Testing Priorities

The highest-value remaining tests are:

1. **Multiple simultaneous remote players**
   - verify P2/P3/P4 remain isolated

2. **Trackball scaling**
   - verify each player's X/Y sensitivity is consumed independently at runtime

3. **Profile migration**
   - test machines with existing `UserProfiles` copies from older revisions

4. **Sunshine lifecycle**
   - start
   - stop
   - restart
   - TeknoParrot-managed state detection

5. **Moonlight lifecycle**
   - executable detection
   - launch behavior
   - reconnect behavior

6. **Non-remote regression testing**
   - confirm Remote Local Play Off behaves exactly like normal local play

7. **Additional IT titles**
   - validate at least one Golden Tee older than 2019
   - PowerPutt
   - Silver Strike
   - Target Toss
   - PuckOff

---

# 18. Known / Expected Limitations

- Sunshine and Moonlight are still external portable packages.
- The beta does not yet provide TeknoParrot-account-based host discovery.
- Remote-player testing has been deepest on Golden Tee Live 2019.
- Full P3/P4 physical-device testing may still be limited depending on available clients/controllers.
- Per-player trackball sensitivity should receive additional runtime verification.
- The remote-play feature should still be considered beta until broader developer testing is complete.

---

# 19. Files Most Relevant to This Branch

```text
TeknoParrotUi.Avalonia/Views/MainView.axaml
TeknoParrotUi.Avalonia/Views/MainView.axaml.cs
TeknoParrotUi.Avalonia/Views/GameSettingsView.axaml
TeknoParrotUi.Avalonia/Views/GameSettingsView.axaml.cs
TeknoParrotUi.Avalonia/Views/JoystickSetupView.axaml
TeknoParrotUi.Avalonia/Views/JoystickSetupView.axaml.cs
TeknoParrotUi.Avalonia/Views/MultiButtonConfigView.axaml
TeknoParrotUi.Avalonia/Views/MultiButtonConfigView.axaml.cs
TeknoParrotUi.Avalonia/Views/RemotePlayManagementView.axaml
TeknoParrotUi.Avalonia/Views/RemotePlayManagementView.axaml.cs

TeknoParrotUi.Common/InputListening/SunshineInputListener.cs
TeknoParrotUi.Common/InputListening/InputListenersManager.cs
TeknoParrotUi.Common/MappingDispatch.cs
TeknoParrotUi.Common/JoystickMapping.cs
TeknoParrotUi.Common/GameProfileLoader.cs
TeknoParrotUi.Common/GameProfiles/
```

---

# 20. Developer Notes

The remote-play implementation should continue following the existing .NET 8/Avalonia architecture rather than reintroducing WPF-specific code.

When changing game-profile structure:

1. preserve existing game-specific settings and mappings
2. update only the required remote-play structure
3. increment `GameProfileRevision`
4. test migration with an existing `UserProfiles` copy
5. verify both Remote Local Play Off and On modes

When debugging remote input, separate the problem into layers:

```text
Moonlight client
    ↓
Sunshine host transport
    ↓
SunshineTeknoParrotInput pipe
    ↓
SunshineInputListener
    ↓
MappingDispatch / player identity
    ↓
TeknoParrot game input / trackball pipe
```

A successful Moonlight stream does not necessarily mean the game-profile binding layer is correct, and a missing control row in the UI does not necessarily indicate a Sunshine transport problem.

---

# Status

**Branch status:** Beta / developer testing

**Primary validated title:** Golden Tee Live 2019

**Current goal:** Produce a stable remote-play test build for broader developer validation before merging the remote-play work further upstream.
