# How Syn3 Updater v3 chooses the install mode

The install mode decides what is written to the USB drive: **AutoInstall** (an `autoinstall.lst` only), **Reformat** or **Downgrade** (a `reformat.lst` as well, which makes the APIM reformat itself first).
A wrong choice can brick the APIM, so the choice is a fixed decision table, not a guess. It is the same logic as v2's `UpdateInstallMode`
(`Syn3Updater/UI/Tabs/HomeViewModel.cs`, line 657 on the `v2` branch), with the options named A to H as in v2, and `InstallModeResolver` is tested against a literal port of that method for every combination of inputs (`My20SafetyTests`).

Words used below: **MY20 protection** is the setting `My20v2`: *Enabled* (true), *Auto-Detect* (null, nothing known) or *Disabled / Not MY20* (false). A **fixed mode** is an install mode the user chose in Settings > Advanced instead of leaving it to the app.

## 1. Which mode is chosen

This runs on Home each time the release or maps selection changes (`InstallModeResolver.Resolve`, called from `HomeViewModel.ResolveInstallMode`). The chart is the intended flow, and `InstallModeFlowTests` writes it out node by node and compares it with v3 for every combination of inputs.

```mermaid
%%{init: {'theme': 'base', 'themeVariables': { 'primaryColor': '#ffffff', 'edgeLabelBackground':'#fff' } } }%%
flowchart TD
    Start(( )) --> Version{Current Version?}

    %% ===== BELOW 3.2 PATH (ORANGE) =====
    Version -->|Below 3.2| Mode30{My20 Protection?}
    Mode30 -->|🔑 MY20 Protected| NotPossible30[[⛔ Not Possible]]
    Mode30 -->|❌ Not MY20 Protected| Reformat30[[♻️ Reformat]]

    %% ===== 3.2 AND NEWER PATH (BLUE) =====
    Version -->|3.2 or newer| Maps{Update Maps?}

    Maps -->|⬆️ Update Maps| Mode34{My20 Protection?}
    Maps -->|☑️ Keep Maps / Non Nav| Autoinstall34[[⚡ Autoinstall]]

    %% ----- Protected Branch (GREEN) -----
    Mode34 -->|🔑 MY20 Protected| ESNAE{ESN Locked?}
    ESNAE -->|🔓 ESN Unlocked| Autoinstall34[[⚡ Autoinstall]]
    ESNAE -->|🔒 ESN Locked| NotPossible34[[⛔ Not Possible]]

    %% ----- Not Protected Branch (PURPLE) -----
    Mode34 -->|❌ Not MY20 Protected| CheckApps{Update Apps?}

    %% Path when keeping existing apps ("Only update maps" release)
    CheckApps -->|✅ Keep Existing| ESNKeep{ESN Locked?}
    ESNKeep -->|🔓 ESN Unlocked| Autoinstall[[⚡ Autoinstall]]
    ESNKeep -->|🔒 ESN Locked| NotPossibleLocked[[⛔ Not Possible]]

    %% Path when updating apps
    CheckApps -->|⬆️ Update App Version| VerCheck{Version?}

    %% Version check outcomes
    VerCheck -->|3.2 up to 3.4.19273| Reformat[[♻️ Reformat]]
    VerCheck -->|3.4.19274 or newer| Downgrade[[⬇️ Downgrade]]

    %% ========== STYLES ==========
    classDef start fill:#4CAF50,stroke:#388E3C,color:white,stroke-width:3px
    classDef question fill:#E3F2FD,stroke:#2196F3,color:#0d47a1,stroke-width:2px
    classDef notPossible fill:#616161,stroke:#424242,color:white,stroke-width:2px
    classDef reformat fill:#FFE0B2,stroke:#FB8C00,color:#000,stroke-width:2px
    classDef autoinstall fill:#C8E6C9,stroke:#388E3C,color:#000,stroke-width:2px
    classDef downgrade fill:#FFCDD2,stroke:#D32F2F,color:#000,stroke-width:2px

    %% Apply classes
    class Start start
    class Version,Mode30,Maps,Mode34,ESNAE,CheckApps,ESNKeep,VerCheck question
    class NotPossible30,NotPossible34,NotPossibleLocked notPossible
    class Reformat30,Reformat reformat
    class Autoinstall34,Autoinstall autoinstall
    class Downgrade downgrade

    %% ===== PATH COLOR CODING =====
    linkStyle default stroke:#666,stroke-width:2px,font-size:14px
    linkStyle 0 stroke:#4CAF50,stroke-width:3px %% Start
    linkStyle 1,2,3 stroke:#FF9800,stroke-width:3px %% below 3.2 path
    linkStyle 4,6 stroke:#2196F3,stroke-width:3px %% 3.2 and newer path
    linkStyle 5,7,8,9 stroke:#4CAF50,stroke-width:3px %% Protected Branch
    linkStyle 10,11,12,13,14 stroke:#7E57C2,stroke-width:3px %% Not Protected Branch
    linkStyle 15,16 stroke:#FB1C00,stroke-width:3px %% Reformat Actions
```

How the chart's boxes map to the app (v2 called the outcomes options A to H):

| Chart | In the app | v2 option |
|---|---|---|
| Below 3.2, not protected: Reformat | installed version under 3200000 | A |
| Below 3.2, protected: Not possible | error shown, Start and Download only disabled | H |
| Keep Maps / Non Nav: Autoinstall | the maps choice is "No maps", "Non-nav APIM" or "Keep existing maps" | B (below 3.4.19274), E (3.4.19274 or newer) |
| Protected, ESN locked: Not possible | the ESN-locked map is dimmed and cannot be picked (its second line says "not compatible with MY20"); if one is already selected, error shown | C-ESN, F-ESN |
| Protected, ESN unlocked: Autoinstall | | C, F |
| Not protected, Keep Existing (apps): Autoinstall / Not possible | the **"Only update maps"** release; ESN-locked maps are greyed out for it too | C, F |
| Not protected, Update App Version: Reformat / Downgrade | a normal release with a map package; Downgrade from 3.4.19274 | D, G |

Notes:
- **MY20 Auto-Detect** (nothing known yet) follows the "Not MY20 Protected" arrows. Because that can be wrong, pressing Start with a Reformat or Downgrade while MY20 is Auto-Detect asks first (diagram 3). Until it is answered, the Home banner says **MY20 Check** instead of Reformat or Downgrade, because that mode may never be used. The question's answers are "Yes or not sure: run the MY20 check" and "No, none of these apply".
- **Version boundaries** are exact: 3.2 is `3200000` and 3.4.19274 is `3419274`; there is no gap between the two Reformat/Downgrade ranges.
- **A fixed mode** (diagram 2) replaces the mode for the *Reformat*, *Downgrade* and *Keep Maps / Non Nav: Autoinstall* outcomes. The other Autoinstall outcomes (protected, or "Only update maps") and *Not possible* ignore it.
- ESN-locked maps are dimmed and cannot be picked whenever MY20 protection is Enabled or the release is "Only update maps" (`InstallModeResolver.EsnBlocked`). Every map in the list shows its ESN state: a lock for ESN locked (pre-MY20 units only), an open lock for not ESN locked (any unit, including MY20). While MY20 is Auto-Detect an ESN-locked map can still be picked (it is not known whether it fits), its line says "we'll check on Start", and pressing Start asks the MY20 question first (diagram 3).

## 2. When a fixed mode exists

v2 only let you fix the mode while MY20 protection was explicitly **off**. v3 keeps that rule in one place (`ProfileSettings`), so reformat or downgrade can never be forced onto a unit that may be MY20.

```mermaid
flowchart TD
    file["Profile file (v2 or v3)<br/>InstallMode text"] --> norm["Normalize:<br/>reformat, autoinstall, downgrade are kept (any case)<br/>anything else, such as v2's translated 'Auto-Detect', becomes autodetect"]
    norm --> gate{"MY20 protection is<br/>Disabled / Not MY20?"}
    gate -- "no (Enabled or Auto-Detect)" --> none["No fixed mode<br/>mode is reset to autodetect"]
    gate -- yes --> has{"A mode is fixed?"}
    has -- no --> none2["No fixed mode<br/>the app chooses (diagram 1)"]
    has -- yes --> forced["Fixed mode in force<br/>ModeForced = true"]

    reset["Things that reset a fixed mode to autodetect:<br/>MY20 set to Enabled or Auto-Detect in Settings<br/>closing 'Edit Advanced Options'<br/>the Interrogator log detecting an MY20 unit<br/>loading a profile that has a fixed mode without MY20 being Disabled"]
    reset -.-> none

    classDef note fill:#eef,stroke:#88a,color:#000,stroke-dasharray: 4 3
    class reset note
```

In Settings the *Install Mode* dropdown is only enabled while *MY20 Protection* is set to Disabled, which is why MY20 is the first dropdown.

## 3. What happens when you press Start

```mermaid
flowchart TD
    go(["Start"]) --> q{"Mode is Reformat or Downgrade,<br/>not 'Download only',<br/>and MY20 is Auto-Detect?"}
    q -- yes --> warn["Warning: 'Answering incorrectly will result in your APIM being bricked.<br/>Is your car, APIM or screen manufactured in 2020 or newer?'"]
    warn -- "Yes (unsure)" --> log["Prepare the Interrogator log USB<br/>nothing is formatted, Start stops here<br/>(the log later sets MY20 to Enabled or Disabled)"]
    warn -- No --> region
    q -- no --> region{"Selected region differs<br/>from the profile's region?"}
    region -- yes --> rc["Confirm the region mismatch"]
    rc -- cancelled --> stop(["Stop"])
    rc -- ok --> apps
    region -- no --> apps{"Reformat or Downgrade<br/>without an APPS package ticked?"}
    apps -- yes --> err["Error: Reformat/Downgrade needs the apps package"] --> stop
    apps -- no --> run["Check free space, confirm and format the USB drive<br/>(or keep it if it is already CYANLABS exFAT MBR),<br/>download, verify, copy"]
    run --> lists{"Mode"}
    lists -- "Reformat or Downgrade" --> two["Write reformat.lst and autoinstall.lst<br/>(downgrade also adds the downgrade tools)"]
    lists -- AutoInstall --> one["Write autoinstall.lst only"]
    two --> done(["Done: put the USB in the car"])
    one --> done

    classDef bad fill:#fdd,stroke:#c33,color:#000
    class err bad
```

## Where it lives

| What | Code |
|---|---|
| The decision table (diagram 1) | `src/Syn3Updater.Core/Services/InstallModeResolver.cs` |
| Calls it and shows the invalid-config error | `HomeViewModel.ResolveInstallMode` |
| Fixed-mode rules (diagram 2) | `ProfileSettings` (`My20v2`, `InstallMode`, `ModeForced`) and `SettingsStore.MigrateProfile`; Settings dropdown rules in `SettingsViewModel` |
| The Start steps (diagram 3) | `HomeViewModel.StartAsync` path, `Workflow.StartAsync`, `UpdateRunner` (which lists are written) |
| Tests | `My20SafetyTests` (decision table against v2, fixed-mode rules), `AppTests` (Home flows) |
