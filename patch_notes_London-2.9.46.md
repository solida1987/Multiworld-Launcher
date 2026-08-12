# Multiworld Launcher 2.9.46

## The engine patches are applied by the launcher

Since 2.9.43 the launcher takes Diablo II's own libraries from your
installation rather than shipping them. Those copies are untouched, and Diablo
II will not load a modded install as they stand — it stops with *"The file data
is corrupt"* before the mod ever runs.

The launcher now applies the mod's own changes to them after copying: 32 bytes
across `Storm.dll`, `D2Glide.dll` and `D2Launch.dll`. Nothing belonging to
Blizzard ships with this project; what ships is the description of those bytes,
applied to the files you already have, on your own machine.

Every edit names the bytes it expects to replace, each file is identified by
the SHA-256 of both its unpatched and patched forms, and the result is verified
before it is kept. A file that isn't the 1.10f build is left untouched and
reported rather than modified, an already-patched file is skipped, and updates
re-apply the patches so a re-copied library can't leave the game unable to
start.
