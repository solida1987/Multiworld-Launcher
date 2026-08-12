# Multiworld Launcher 2.9.45

## Diablo II install detection

- **Game.exe is now checked by version rather than by size.** Diablo II ships
  Game.exe wrapped in copy protection, and some launchers and version switchers
  keep a build without that wrapper, so a working 1.10f installation may
  legitimately contain either one. The launcher supplies no Game.exe, modifies
  none and recommends none — it only needs to know the patch level is 1.10f,
  and both report that. Whichever one is in your folder is used as-is.
- `default.key` is no longer required. It holds your key bindings and the game
  writes it on first run, so a fresh installation has none to copy — this made
  install refuse an otherwise perfectly good 1.10f folder.
- The wrong patch level is still refused, with the version it found named in
  the message instead of a byte count.

## Cleanup

- The first-run cleanup now also repairs Diablo II's registry entries. Playing
  the mod points the game's save path at the mod's own folder; left behind, a
  plain Diablo II install goes looking for saves in a folder that may no longer
  exist and fails to start. Only paths that lead into this launcher's own
  folder are touched — a save path you set yourself is left alone.
