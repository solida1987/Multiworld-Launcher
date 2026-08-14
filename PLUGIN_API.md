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
  "apiVersion": 1,
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
| `apiVersion` | must be **1**. Wrong value = refused, with a message saying which side is out of date |
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

Publish it wherever you like — your repo's releases are the obvious place. The
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

### Identity

| Member | Notes |
|---|---|
| `GameId` | must equal `plugin.json`. A mismatch is refused at load |
| `DisplayName`, `Subtitle` | shown in the library |
| `IconPath` | absolute path to a 256×256 PNG — ship it in your package |

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

### The Archipelago bridge

Two shapes, and picking the right one matters.

**The launcher owns the connection** (the usual case, and what Diablo II does).
You raise `LocationsChecked` when the game completes checks; the launcher
forwards them. The launcher calls `ReceiveItemsAsync` when items arrive. You
raise `GoalCompleted` when the goal is met. Leave `ConnectsItself` false.

**Your game owns the connection.** Set `ConnectsItself => true`. An AP server
allows one connection per slot and kicks the older one, so the launcher then
stays off the slot entirely: it launches with credentials filled in and leaves
you alone. `LocationsChecked` and `ReceiveItemsAsync` are unused in this mode.

`OnApStateChanged` tells you connected/disconnected either way.

### UI

`CreateSettingsPanel` and `CreateMapTrackerPanel` are called **on the UI thread
only**. Build WPF elements there and nowhere else. Touching a `UIElement` from
a background thread is the second most common plugin bug.

Return null from `CreateSettingsPanel` if you have no settings.
`CreateMapTrackerPanel` is only called when `SupportsMapTracker` is true.

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

`Plugins/DiabloII/D2Plugin.cs` in the launcher's public source is a complete,
shipped implementation: named-pipe bridge to the game, installer with progress,
verification against a manifest, settings page, map tracker, standalone mode.
It is compiled in rather than loaded from disk, but it implements the same
interface, and every question about "what is this member for" is answered there
by working code.

---

## 9. Versioning

`apiVersion` is `1`. It is bumped whenever `IGamePlugin` changes in a way that
breaks existing plugins, and the launcher refuses a mismatch outright rather
than loading a plugin that will fail halfway through. The error says which side
is out of date.

---

*Questions about the API belong on the launcher's repository. Questions about
whether your game is allowed belong to Archipelago — read their rules.*
