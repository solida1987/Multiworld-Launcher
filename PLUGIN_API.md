# Writing a plugin for Multiworld Launcher

A plugin adds one game to the launcher and gets everything the built-in games
get: install, update, verify, the Archipelago connection, checks and items, a
settings page of its own, and a map tracker. Diablo II is implemented as one.

You write it, you publish it, you own it. **The launcher hosts no plugins, lists
none, links to none and reviews none.** There is no store and there will not be
one. A player who wants your plugin gets it from you.

---

## Your responsibility

A plugin is your software, distributed by you. Multiworld Launcher takes no
responsibility for what one does.

If your game is played through Archipelago, the Archipelago Discord's rules
apply to **your** project — not to this launcher. You are expected to know them.
They are pinned in that server's `#rules` channel, together with the Content and
Copyright Rules, the Ratings Rules and the After Dark policy. In particular:

- **Do not distribute anything you do not have the rights to distribute.** Patch
  the player's own copy instead of shipping game files.
- **Check the age rating before you start.** A game with no rating anywhere is
  treated the same as an 18+ game and belongs in Archipelago After Dark, not the
  main server. That one catches people out.
- **Anything banned from that server must not be reachable through your plugin.**
- **Derivative assets must never download automatically.** The player has to
  agree to the installation explicitly.
- **Disclose your use of AI tools**, in good faith.

Setting `"rulesAcknowledged": true` in `plugin.json` is your statement that you
have read the rules that apply to your project and that your plugin follows
them. The launcher will not load a plugin without it.

---

## 1. The project

A plugin is a .NET class library targeting the same framework as the launcher.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AssemblyName>MyGame.Plugin</AssemblyName>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Multiworld Launcher">
      <HintPath>path\to\Multiworld Launcher.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

**`<Private>false</Private>` is not optional.** It stops the launcher assembly
being copied next to your plugin. If a copy travels with your plugin, the
launcher loads *that* copy, your `IGamePlugin` becomes a different type from the
launcher's — same name, same shape, different identity — and every call fails
with a message that reads like nonsense: *cannot convert IGamePlugin to
IGamePlugin*. This is the single most common way a plugin fails to load.

`EnableDynamicLoading` makes the build emit the `.deps.json` the launcher uses
to resolve your own dependencies from your own folder.

Your entry class must be **public**, implement `IGamePlugin`, and have a
**public parameterless constructor**.

---

## 2. `plugin.json`

Sits at the top of the package. The launcher reads it **before** running any of
your code — it is what the player is shown when deciding whether to trust you.

```json
{
  "apiVersion": 2,
  "gameId": "mygame_archipelago",
  "displayName": "My Game",
  "subtitle": "Randomiser Mod",
  "version": "1.0.0",
  "author": "your name or handle",
  "authorContact": "where a player can reach you",
  "assembly": "MyGame.Plugin.dll",
  "entryType": "MyGame.Plugin.MyGamePlugin",
  "declares": {
    "installsFiles": true,
    "downloadsFrom": ["github.com/you/yourgame"],
    "runsExternalProcess": true,
    "connectsToAp": true,
    "requiresOriginalGame": true
  },
  "rulesAcknowledged": true
}
```

| Field | Rule |
|---|---|
| `apiVersion` | must be **2**. Wrong value = refused, with a message saying which side is out of date |
| `gameId` | `^[a-z0-9][a-z0-9_]{1,63}$`. Becomes a folder name and a registry key. Must equal your `IGamePlugin.GameId` |
| `assembly` | a plain `.dll` filename. No `/`, `\`, `:` or `..` |
| `entryType` | full namespace-qualified type name |
| `author` | required. The player has to know whose code this is |
| `rulesAcknowledged` | must be `true`. See above |

`declares` is shown to the player verbatim. Nothing enforces it — the launcher
cannot sandbox .NET and does not pretend to. It is your statement, on screen,
and something to be held to.

---

## 3. Packaging

A `.londonplugin` file is a zip:

```
MyGame.londonplugin
├── plugin.json
├── MyGame.Plugin.dll
├── MyGame.Plugin.deps.json
├── icon.png                ← 256×256
└── (your own dependencies)
```

Build it with `Tools/pack_plugin.py`, which takes the project directory:

```
python Tools/pack_plugin.py ../MyGame-London-Plugin -o dist
```

It packs an explicit list, not the whole `bin` folder, and **refuses** to
include anything that looks like a second copy of the launcher. That is not
tidiness: building against the launcher copies its assembly in beside yours
(`Private=false` does not stop it for a WinExe reference), and shipping that
copy would make `IGamePlugin` from your plugin a different type from
`IGamePlugin` in the host. The cast then fails with a message saying your class
does not implement an interface it visibly implements.

### More than one channel from one build

A game with a stable and a testing channel is two packages from one assembly:
two manifests, each naming its own entry class, each with its own `gameId`.

```
python Tools/pack_plugin.py ../MyGame-London-Plugin -m plugin.stable.json
python Tools/pack_plugin.py ../MyGame-London-Plugin -m plugin.testing.json
```

The channel has to be the **class**, not a flag someone sets from outside: the
loader constructs your entry type through a parameterless constructor, and
there is no object initialiser to set anything.

Publish wherever you like — your repo's releases are the obvious place. The
player downloads it and uses **Add plugin** in the launcher.

---

## 4. What happens when a player adds it

1. The launcher **hashes** the file and **reads `plugin.json`**. Nothing is
   extracted and nothing runs.
2. The player is shown who you are, what you declared, and the hash, with a
   plain warning that this is not the launcher's code and it runs with their
   rights. The approve button is disabled for a few seconds.
3. Only after a yes is the package unpacked into `GamePlugins\<gameId>\`.
4. The hash of the **installed folder** is recorded.
5. Your assembly is loaded into its own collectible `AssemblyLoadContext`.

At every later start the folder is hashed again. **If it has changed, the plugin
does not load** and the player is asked to approve it again. This is normal
after an update — it is new code either way.

---

## 5. `IGamePlugin`

The interface is **69 members**, and you will implement a handful of them.

That is the design: almost everything has a default, and the default says "this
game does not do that". A plugin that answers nothing still loads, still
appears in the library, and still installs and launches — it just has no map
tab, no known-issue card, no commands, no achievements of its own. **Holes you
do not fill are holes the launcher does not draw.**

### What you MUST write

Two groups, and only two.

**Identity and lifecycle have no sensible default** — the launcher cannot guess
your game's name or how to install it:

`GameId`, `DisplayName`, `Subtitle`, `IconPath`, `InstalledVersion`,
`AvailableVersion`, `GameDirectory`, `IsInstalled`, `IsRunning`,
`CheckForUpdateAsync`, `InstallOrUpdateAsync`, `VerifyInstallAsync`,
`LaunchAsync`, `StopAsync`, `ReceiveItemsAsync`, `OnApStateChanged`,
`CreateSettingsPanel`, `Description`, `ApWorldName`, `ThemeAccentColor`,
`GameBadges`, `GetNewsAsync`.

**The six events must be declared even if you never raise them.** This is a C#
limitation, not a design choice: an interface member can carry a default
implementation, but an *event* cannot. Six one-line declarations are the price
of admission, and the compiler will not let you skip them:

```csharp
public event Action<long[]>? LocationsChecked;
public event Action<int>?    GameExited;
public event Action?         GoalCompleted;
public event Action<long[]>? LocationsMissing;
public event Action<string>? StandaloneItemReceived;
public event Action<string>? LogLine;
```

Everything else below is optional.

### Identity

| Member | Notes |
|---|---|
| `GameId` | must equal `plugin.json`. A mismatch is refused at load |
| `DisplayName`, `Subtitle` | shown in the library |
| `IconPath` | absolute path to a 256x256 PNG — ship it in your package |
| `HeaderArtPath` | wide banner behind the game page header. Null = a plain tint |

### Version and state

`InstalledVersion`, `AvailableVersion`, `GameDirectory`, `IsInstalled`,
`IsRunning`.

These are read on **every** library refresh, several times a second while the
UI is open. Keep them cheap: cache, do not hit the disk or the network here.

### Lifecycle

| Member | Contract |
|---|---|
| `CheckForUpdateAsync` | must not throw on network failure — set `AvailableVersion` to null and return |
| `InstallOrUpdateAsync` | **must be idempotent.** It is called again when already up to date |
| `VerifyInstallAsync` | check only; do not repair. The caller decides |
| `ValidateExistingInstall` | null = the folder is fine; otherwise a short reason shown to the player |
| `LaunchAsync` | the launcher's AP session is already connected when this is called |
| `LaunchStandaloneAsync` | only used when `SupportsStandalone` is true |
| `StopAsync` | stop cleanly, then make sure the process is gone |

### The base game, if your mod needs one

For a mod that installs on top of a game the player already owns.

| Member | Notes |
|---|---|
| `InstallCapability` | `AutoMod` for this shape; drives the badge and the buttons |
| `NeedsBaseGameFolder()` | return a request, or null. **Try to find it yourself first** — nobody should hunt for a folder you could have located |
| `ValidateExistingInstall` | called on the folder the player picked, before you are told about it |
| `SetBaseGameFolder(folder)` | it was accepted. Persist it yourself; the launcher keeps no copy |
| `HasBaseGameFiles()` | false sends the launcher back through the request and a reinstall |
| `IsFreeGame`, `PurchaseUrl` | where the base game comes from, and what the button says |

Never modify the player's original install. Copy out of it.

### Components

Anything your game needs *beside* its own files — a renderer wrapper, a sound
driver, a resolution patch.

`DetectComponents()` reports them; the launcher draws one badge each and
**refuses to launch** while anything `Required` is missing.

`DetectComponentsAdopting()` is the same list, but you may first copy across
anything you find in the player's own copy of the base game. The two are
separate on purpose: the first is called to *decide* things, and a question
should not rearrange the disk while answering it.

### Files, repair and antivirus

| Member | When |
|---|---|
| `ScanInstallProblemsAsync` | thorough per-file integrity pass. **null means "could not tell"** — never treat that as failure |
| `RepairFilesAsync` | restore the named files |
| `GetMissingCriticalFiles` | the short list the game cannot start without. Checked right before launch |
| `MissingCriticalFilesCause` | one sentence on what usually removes them, shown in the repair prompt |
| `RepairMissingCriticalFilesAsync` | put them back |
| `TryHandleAntivirusBlockAsync` | a launch failed and looks like antivirus. Return true if you showed your own UI |

Antivirus quarantine strikes *between* the install and the launch, which is why
the critical-file check is separate from the integrity scan and runs later.

### The Archipelago bridge

Two shapes, and picking the right one matters.

**The launcher owns the connection** (the usual case). You raise
`LocationsChecked` when the game completes checks; the launcher forwards them.
The launcher calls `ReceiveItemsAsync` when items arrive. You raise
`GoalCompleted` when the goal is met. Leave `ConnectsItself` false.

**Your game owns the connection.** Set `ConnectsItself => true`. An AP server
allows one connection per slot and kicks the older one, so the launcher then
stays off the slot entirely: it launches with credentials filled in and leaves
you alone. `LocationsChecked` and `ReceiveItemsAsync` are unused in this mode.

`OnApStateChanged` tells you connected/disconnected either way.

#### Asking things of the live connection

`OnApServicesAttached(IApServices?)` hands you one object with everything you
can ask of the session: your own slot, the seed's `slot_data`, the seed name,
player names, which locations are checked and which are not, scouting, a
resync, and the DeathLink send path. Null means the session ended.

Take a reference and use it; do not cache what it returns.

`OnApSessionChanged(ApSessionContext?)` is the *presentation* half — slot name,
hint points, hint cost, and the item and location names **this seed actually
contains**. That last distinction is the point: a game's package may define
2730 locations while a given seed uses 833, and offering the other 1897 as
buttons means offering 1897 server errors.

#### Without a server

A standalone run gets no DataPackage and no location universe, so it can only
show checks that have already fired — unless you answer:

| Member | Notes |
|---|---|
| `GetLocationDataPackage()` | your own id to name table, in DataPackage shape |
| `GetStandaloneLocationUniverse()` | every location that *exists* in the run about to start. Called after `LaunchStandaloneAsync`, because the answer depends on the settings that launch just wrote |
| `StandaloneItemReceived` | raise it when your game hands the player something itself |

#### DeathLink

Send: `IApServices.ReportDeath(cause)` — it checks the opt-in for you.
Receive: `OnDeathLinkReceivedAsync(source, cause)`.
`SendsDeathLink => true` makes the DeathLink achievement reachable; leave it
false and the launcher will not offer an achievement nobody can earn.

### Achievements

The launcher generates a ladder for every game from what it already counts:
installs, connects, checks, goals, sessions, playtime. Four members are yours.

| Member | Notes |
|---|---|
| `AchievementIdPrefix` | **FROZEN once shipped.** Earned achievements are stored by id; changing this un-earns everything a player has |
| `GoalAchievement` | replaces the generic "complete your first goal" with your game's actual win condition |
| `ExtraAchievements` | your own, with unlock rules that read the launcher's bookkeeping |
| `SendsDeathLink` | see above |

### Commands and pages

| Member | Notes |
|---|---|
| `GetCommands()` | named buttons in the Overview row. You are handed the window; open whatever you like. `NeedsInstall: false` for the things done *before* installing |
| `ItemActions` | the Items tab's hint/cheat entry point. Null hides the button |
| `CreateSettingsPanel` | UI thread only. Null = no gear icon |
| `SupportsMapTracker` + `CreateMapTrackerPanel` | UI thread only. False = no Map tab |
| `KnownIssues` | bugs with a workaround, shown as a card. Empty hides it |
| `Credits` | one line each, as many as you like. Empty hides them |

`CreateSettingsPanel` and `CreateMapTrackerPanel` are called **on the UI thread
only**. Build WPF elements there and nowhere else. Touching a `UIElement` from
a background thread is the second most common plugin bug.

### Settings of your own

`PluginSettings.Get/Set(GameId, key, value)` — a small string bag kept inside
the launcher's own settings file, keyed by your `GameId`. The launcher never
reads what is in it.

Use it rather than inventing your own file: the player's settings stay in one
place, and one place is what a backup copies.

### Presentation

`Description`, `ApWorldName`, `ThemeAccentColor`, `GameBadges` are used.

**`VideoPreviewUrl` and `ScreenshotUrls` are ignored for plugins.** Both are
URLs, and honouring them would mean the launcher fetching media from an address
a third party chose. Show what you shipped inside your package, which the player
already saw when they approved it, or show nothing.

`BuiltAgainstDataPackageChecksum` — the AP datapackage checksum your integration
was built from. The launcher compares it with what the server announces and
warns the player when your apworld has moved on. Return null if that does not
apply.

### Saying something

`LogLine` is your line into the launcher's log. You write the whole line,
prefix and all — the launcher does not decorate it, because you know what you
did and it does not.

Without this, work you do on your own — a save repair, a file put back —
happens in silence while everything else is narrated.

---

## 6. If your plugin throws

Every call into your code is wrapped. The first exception **quarantines** the
plugin: it stops being called, its game shows the reason, and the rest of the
launcher carries on. It is not restarted until the launcher is.

That includes properties and events. A plugin that throws inside
`LocationsChecked` would otherwise take the AP thread with it.

Cancellation is not a fault — `OperationCanceledException` passes through
untouched, because a player pressing Stop must not brand your plugin broken.

**Handle your own errors.** Quarantine is a backstop so one plugin cannot break
the launcher; it is not error handling, and from the player's side it looks like
your game stopped working for no reason.

---

## 7. The five mistakes everyone makes

1. **Forgetting `<Private>false</Private>`** → *cannot convert IGamePlugin to
   IGamePlugin*. See §1.
2. **Building UI off the UI thread** → an `InvalidOperationException` that
   quarantines you on first open.
3. **`InstallOrUpdateAsync` not idempotent** → reinstalls, or corrupts a good
   install, when called on an up-to-date game.
4. **`GameId` not matching `plugin.json`** → refused at load. The manifest is
   the half the player read; it wins.
5. **Expensive property getters** → the library refresh stutters. They are read
   constantly.

---

## 8. A worked example

**Diablo II Archipelago** is a complete, shipped plugin built exactly this
way: named-pipe bridge to the game, installer with progress, verification
against a manifest, settings page, map tracker, standalone mode, save repairs,
its own antivirus handling, two channels from one assembly.

It is worth knowing that it used to be compiled into the launcher, and moving
it out was how this interface got finished. Every hole documented above exists
because that move needed it — the launcher had a Diablo-shaped hole in 32
places, and each one became a member here. When the last one was gone, the
launcher shipped with no games at all.

So the answer to "can a plugin do X" is: if the built-in game could, a plugin
can, because there is no longer any difference between the two.

---

## 9. Versioning

`apiVersion` is `2`. It is bumped whenever `IGamePlugin` changes in a way that
breaks existing plugins, and the launcher refuses a mismatch outright rather
than loading a plugin that will fail halfway through. The error says which side
is out of date.

Separately from the API version, a plugin carries a .NET reference to the exact
launcher assembly it was compiled against. A newer launcher satisfies an older
reference, so a plugin keeps working as the launcher updates — but the reverse
does not hold. A plugin built against launcher 3.0.1 will not load on 3.0.0, so
say which launcher version yours needs.

---

*Questions about the API belong on the launcher's repository. Questions about
whether your game is allowed belong to Archipelago — read their rules.*
