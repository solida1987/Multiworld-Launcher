# Multiworld Launcher 2.8.2

## New

- **Diablo II: LoD (Experimental)** now appears in Browse Games as a separate,
  optional install. It tracks the experimental mod build (its own folder and
  repo) for aggressive testing of new and unstable features — it can break, so
  it is kept fully apart from the stable Diablo II install. Not recommended for
  a real playthrough.

## Fix

- Fixed a harmless crash log that could be written when closing the launcher: in
  some cases the shutdown sequence raced the window close and threw on exit. The
  launcher now exits cleanly.
