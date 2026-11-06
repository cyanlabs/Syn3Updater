# How Syn3 Updater v3 chooses the install mode

The install mode decides what is written to the USB drive: **AutoInstall** (an `autoinstall.lst` only), **Reformat** or **Downgrade** (a `reformat.lst` as well, which makes the APIM reformat itself first).
A wrong choice can brick the APIM, so the choice is a fixed decision table, not a guess. It is the same logic as v2's `UpdateInstallMode`
(`Syn3Updater/UI/Tabs/HomeViewModel.cs`, line 657 on the `v2` branch), with the options named A to H as in v2, and `InstallModeResolver` is tested against a literal port of that method for every combination of inputs (`My20SafetyTests`).

Words used below: **MY20 protection** is the setting `My20v2`: *Enabled* (true), *Auto-Detect* (null, nothing known) or *Disabled / Not MY20* (false). A **fixed mode** is an install mode the user chose in Settings > Advanced instead of leaving it to the app.

## 1. Which mode is chosen

This runs on Home each time the release or maps selection changes (`InstallModeResolver.Resolve`, called from `HomeViewModel.ResolveInstallMode`).

```mermaid
flowchart TD
    start(["Region, release and maps are chosen"]) --> ver{"Installed Sync version<br/>below 3.2 (3200000)?"}

    ver -- yes --> my1{"MY20 protection<br/>Enabled?"}
    my1 -- yes --> H["<b>Option H</b><br/>AutoInstall<br/>INVALID: error shown, Start disabled"]
    my1 -- "no (Auto-Detect or Disabled)" --> A["<b>Option A</b><br/>Reformat"]

    ver -- no --> maps{"Maps choice is<br/>No maps, Non-nav APIM<br/>or Keep existing maps?"}
    maps -- yes --> BE["<b>Option B</b> (below 3.4.19274)<br/><b>Option E</b> (3.4.19274 or newer)<br/>AutoInstall"]

    maps -- "no: a map package" --> esn{"MY20 Enabled and<br/>the map release is ESN-locked?"}
    esn -- yes --> CE["<b>Option C-ESN / F-ESN</b><br/>INVALID: error shown, Start disabled<br/>mode left as it was"]
    esn -- no --> cf{"MY20 Enabled, or the release is<br/>'Only update maps'?"}
    cf -- yes --> CF["<b>Option C</b> (below 3.4.19274)<br/><b>Option F</b> (3.4.19274 or newer)<br/>AutoInstall"]
    cf -- no --> newer{"Version 3.4.19274<br/>or newer?"}
    newer -- no --> D["<b>Option D</b><br/>Reformat"]
    newer -- yes --> G["<b>Option G</b><br/>Downgrade"]

    A -.-> fixed
    BE -.-> fixed
    D -.-> fixed
    G -.-> fixed
    fixed["A fixed mode (diagram 2), if there is one,<br/>replaces the mode above for A, B, D, E and G.<br/>C, F and H ignore it: always AutoInstall."]

    classDef bad fill:#fdd,stroke:#c33,color:#000
    classDef note fill:#eef,stroke:#88a,color:#000,stroke-dasharray: 4 3
    class H,CE bad
    class fixed note
```

| Option | Installed version | MY20 protection | Maps / release | Mode | Fixed mode used? |
|---|---|---|---|---|---|
| A | below 3.2 | Auto-Detect or Disabled | anything | Reformat | yes |
| H | below 3.2 | Enabled | anything | AutoInstall, **invalid** | no |
| B | 3.2 up to 3.4.19273 | anything | no map package | AutoInstall | yes |
| E | 3.4.19274 or newer | anything | no map package | AutoInstall | yes |
| C-ESN, F-ESN | 3.2 or newer | Enabled | ESN-locked map | **invalid** (mode unchanged) | n/a |
| C | 3.2 up to 3.4.19273 | Enabled, or "Only update maps" | map package | AutoInstall | no |
| F | 3.4.19274 or newer | Enabled, or "Only update maps" | map package | AutoInstall | no |
| D | 3.2 up to 3.4.19273 | Auto-Detect or Disabled | map package, normal release | Reformat | yes |
| G | 3.4.19274 or newer | Auto-Detect or Disabled | map package, normal release | Downgrade | yes |

ESN-locked maps are also greyed out in the maps list whenever MY20 protection is Enabled or the release is "Only update maps".

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
