# Multiworld Launcher 2.9.52

## The renderer is chosen by what is installed

Diablo II Archipelago no longer bundles **D2GL**, **SGD2FreeRes** or **DSOAL** —
the HD renderer, the free-resolution patch and the 3D audio driver. They are
licensed GPL-3.0, AGPL-3.0-or-later and LGPL-2.1, and those licences place
conditions on *distributing* the software that the project was not meeting.
They are optional add-ons, and the game runs without all three; the game's
README now explains where to get each one and where the files go.

The launcher used to start the game with `-3dfx` unconditionally, which only
works because D2GL was in the folder. It now looks for `glide3x.dll` each time
it launches: found, and it starts with the Glide renderer so D2GL takes over;
not found, and it starts on DirectDraw instead. There is no setting for this
and nothing to configure — install the component and it is used, don't and it
isn't.

**An existing install is left alone.** The launcher only ever removes files it
installed itself, so a D2GL you already have keeps working exactly as before.

## …and it tells you which ones it found

Installing something by hand raises a question the launcher had no answer to:
*did that work?* It answers it in three places now.

**Settings → Diablo II Archipelago** has an **Optional add-ons** section: all
three components, a status each, what they add, and a link to the author.
A **Check again** button re-reads the game folder, so you can alt-tab out, copy
the files in, come back and click it — no restart.

**The game's Overview page** shows a small grey badge — `ADD-ONS: D2GL, DSOAL`,
or `NO OPTIONAL ADD-ONS`. It never counts against **READY TO PLAY**: these are
optional and the game is ready without them.

**The Play log** prints the same three lines at every launch, right above the
install check.

Two cases are worth calling out, because both look like a broken download when
they are not:

- **SGD2FreeRes installed on its own does nothing.** Nothing in the mod loads
  it — D2GL does, through `load_dlls_late` in `d2gl.ini`. The launcher reports
  it as *"Installed but inactive — nothing is loading it"* and says to install
  D2GL as well, instead of claiming it is working.
- **DSOAL needs both of its files.** `dsound.dll` without `dsoal-aldrv.dll` is a
  half-finished copy, and it is reported as missing rather than as installed.

The graphics settings dialog also says so now: it edits `d2gl.json`, which ships
with the mod, so it works before D2GL is installed — it just notes that the
settings have nothing to apply to yet and will take effect once you install it.

## Also

- `NOTICE` and `THIRD-PARTY-NOTICES.md` now list only the components that
  actually travel with the project, and say plainly which ones were removed and
  why. Their licence texts left with them, so the package carries the licence of
  exactly what is in it.
- The launcher's logo credit is unchanged: the Archipelago logo © 2022 by Krista
  Corkos and Christopher Wilson, CC BY-NC 4.0, adapted.
