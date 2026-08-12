# Multiworld Launcher 2.9.43

## Diablo II: the engine now comes from your own installation

Nothing belonging to Blizzard ships with this project any more. Previously the
Diablo II package contained the 1.10f engine binaries; it no longer does, and
neither does the launcher.

Instead, the launcher copies them out of your own Diablo II installation, the
same way it has always copied the MPQ data files.

- **You need a Diablo II + Lord of Destruction installation patched to 1.10f.**
  The mod hooks fixed addresses inside that engine, so 1.13c and 1.14 cannot
  work. (1.14 also merges the engine DLLs into the main executable, so the
  files are not present there at all.)
- Every engine file is verified by exact size before it is copied, so pointing
  the launcher at the wrong patch level now fails with an explanation instead
  of installing something that crashes on start.
- **Existing installations are unaffected.** Files already in place and correct
  are never replaced, so updating does not disturb a working install and does
  not require you to have a 1.10f folder on hand.
