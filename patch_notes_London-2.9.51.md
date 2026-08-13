# Multiworld Launcher 2.9.51

## The updater now says what it did

An update failed on a tester's machine and left nothing behind to explain it.
Everything worked: the package downloaded whole, the checksum verified, the
archive extracted, the update script was written to disk — and then the
launcher was still on the old version, with no error, no log and no message.
Every one of those steps could be reproduced by hand afterwards, which is the
least useful kind of evidence there is.

The reason nothing was known is that nothing was recorded. The update script
only ever wrote a note in one specific failure, and the launcher caught every
error during the update without saying a word — so an update that quietly did
nothing looked exactly like an update that was never offered.

Every stage now writes a line to `multiworld_launcher_update.log` in your temp
folder: the download starting and finishing, the checksum, the extraction, and
whether the update script actually started — including its process id, or the
exact error if it refused to start. If an update ever does nothing again, that
file will say where it stopped.

## A stalled download can no longer hang forever

The download had no deadline on reading the data itself, only on the initial
connection. A connection that died mid-transfer would leave the launcher
waiting on the splash screen at a frozen percentage, with nothing to cancel and
nothing to time out — indefinitely.

Each read now has its own 60-second deadline. A stalled download stops with a
message telling you to check your connection and try again, instead of sitting
there looking like the launcher has crashed.

## Also

- A failed update never stops the launcher from starting: it opens on the
  current version, as before, but the reason is now written to the log.
- The README now carries an AI usage disclosure.
