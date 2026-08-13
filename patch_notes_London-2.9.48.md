# Multiworld Launcher 2.9.48

Work that was finished during the last test run and held back so it could not
disturb a game in progress. It works with the Diablo II release already
published — no game update is needed.

## The tracker now tells you where your gate key is

When a run stalls because an act gate will not open, the in-game tracker no
longer just says the key is missing: it names the location holding it. Act keys
are progressive — several copies of one item, where the Nth copy opens the Nth
gate — so the launcher works out which copy you are waiting for and looks up
that specific placement. A key sitting in another player's world is reported as
such rather than guessed at.

## Known issues card

The game's Overview page can now carry notes about bugs that come from Diablo II
itself, where the answer is "yes, that happens, and here is what you do".

The first entry is the Act 2 waypoint bug: your activated waypoints vanish from
the map. It is vanilla 1.10 behaviour, not save corruption and not the mod —
the game withholds the Act 2 waypoints while Jerhyn's palace dialogue is
pending. Talk to Jerhyn outside the palace in Lut Gholein and they come back.

## Gate keys in other players' worlds

A new option controls whether act gate keys may be placed in other players'
worlds. It is **off** by default, which keeps every key inside your own game —
the behaviour so far. Turning it on lets the generator spread them across the
multiworld. The option is Archipelago-only and is shown disabled in standalone,
where there are no other worlds to place them in.

## Fixes

- **Unique items kept their level requirements** even with level requirements
  turned off. `UniqueItems2.txt` names the column `LevelReq` and was simply
  missing from the list of tables to patch. Found by scanning every data table
  for a level-requirement column rather than trusting that list to be complete.
- **Bigger quivers.** Arrows and bolts now stack to 511 instead of 350 and 250.
  511 is not a compromise — it is the engine's own ceiling, since the save
  format gives an item's quantity 9 bits and the game clamps to it in four
  separate places. A table asking for more would simply be ignored.
- **A hunt that cannot be placed is now pinned** rather than shuffled into a
  spot it cannot occupy.

## Credits

The contributor credit line now carries a role alongside the name, so people
are credited for what they actually did. Maegis is credited for evil minion
bookkeeping and answering questions.
