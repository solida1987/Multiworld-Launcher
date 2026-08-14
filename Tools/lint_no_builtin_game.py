# -*- coding: utf-8 -*-
r"""Gate: the launcher must not know any game by name.

A game becomes a plugin the moment nothing in the launcher asks for it by type.
Until then it is built in, whatever folder it sits in -- and moving the files
first only means the game is broken while the interface is being designed, on
the one integration that must not break.

So this counts what is left. Every hit is a place where the launcher reaches
past IGamePlugin into a specific game, and each one has to become interface
surface before the game can move out.

    python Tools/lint_no_builtin_game.py            # report
    python Tools/lint_no_builtin_game.py --max 46   # fail above a ceiling

Point --max at the current count and lower it as the work lands; the gate then
fails the moment a new type check is added.
"""
import argparse
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Type names that belong to a specific game rather than to the launcher.
GAME_TYPES = re.compile(
    r"\b("
    r"D2Plugin|D2MapTracker|D2SeedCheck\w*|D2Yaml\w*|D2Standalone\w*|"
    r"D2ItemAction\w*|D2ApAction\w*|D2GateKey\w*|D2SeedLibrary|D2GL\w*|"
    r"D2LocationUniverse|D2LogicTables|D2DataFiles|D2EnginePatch|"
    r"D2RandomizerSettings|D2RandomizeProgress"
    r")\b")

# Where a game's own code is allowed to live. Everything else is the launcher.
ALLOWED_PREFIXES = ("Plugins" + os.sep + "DiabloII", "Tools", "bin", "obj",
                    ".claude", "dist")

EXTENSIONS = (".cs", ".xaml")


def scan():
    hits = []
    for base, dirs, files in os.walk(ROOT):
        rel_base = os.path.relpath(base, ROOT)
        if rel_base.startswith(ALLOWED_PREFIXES):
            dirs[:] = []
            continue
        dirs[:] = [d for d in dirs
                   if not os.path.relpath(os.path.join(base, d), ROOT).startswith(ALLOWED_PREFIXES)]
        for f in files:
            if not f.endswith(EXTENSIONS):
                continue
            path = os.path.join(base, f)
            rel = os.path.relpath(path, ROOT)
            try:
                text = io.open(path, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            for n, line in enumerate(text.split("\n"), 1):
                stripped = line.strip()
                # A comment that mentions a type is not a dependency on it.
                if stripped.startswith(("//", "///", "<!--", "*")):
                    continue
                m = GAME_TYPES.search(line)
                if m:
                    hits.append((rel, n, m.group(1), stripped[:88]))
    return hits


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--max", type=int, default=None,
                    help="fail when more than this many references remain")
    args = ap.parse_args()

    hits = scan()
    by_file = {}
    for rel, n, name, line in hits:
        by_file.setdefault(rel, []).append((n, name, line))

    for rel in sorted(by_file, key=lambda r: -len(by_file[r])):
        print("  %-34s %3d" % (rel, len(by_file[rel])))
        for n, name, line in by_file[rel][:4]:
            print("      %5d  %s" % (n, line))
        if len(by_file[rel]) > 4:
            print("      ... og %d mere" % (len(by_file[rel]) - 4))

    print()
    print("%d referencer til et bestemt spil uden for dets egen mappe" % len(hits))

    if args.max is None:
        return 0
    if len(hits) > args.max:
        print("FEJL: loftet er %d. Hver reference skal blive til interface-flade" % args.max)
        print("      i IGamePlugin, foer spillet kan flytte ud som plugin.")
        return 1
    if len(hits) < args.max:
        print("OK -- og under loftet paa %d. Saet --max ned til %d."
              % (args.max, len(hits)))
    else:
        print("OK: paa loftet (%d)." % args.max)
    return 0


if __name__ == "__main__":
    sys.exit(main())
