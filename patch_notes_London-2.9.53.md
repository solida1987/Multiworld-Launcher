# Multiworld Launcher 2.9.53

## It tells you when the game is going to ask for the CD

If your own `Game.exe` is the original disc-protected build, Diablo II stops and
asks for the disc. Until now the launcher said nothing about it, so that looked
like the mod was broken rather than the game doing exactly what it shipped
doing.

The Play log now says so before it happens: keep your disc in the drive. The
launcher does not modify `Game.exe`, does not ship one, and does not recommend
one — which build sits in your folder is your business. It only checks that the
patch level is 1.10f, and it reads that from the version resource, which is
there either way.

Detection is by reading the executable's section table. Nothing here touches or
defeats the protection.
