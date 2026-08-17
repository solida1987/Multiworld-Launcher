# -*- coding: utf-8 -*-
"""Gate: the launcher must not carry, fetch, or ship a list of games.

Two separate things went wrong before, and this checks for both.

The first is code that ASKS a server which games exist. There was a method
that fetched archipelago.gg's datapackage and built a card for every game the
AP server knew about, attributing them to "Archipelago Community". It was
never called -- but it was one line from being live, and "not called yet" is
not a property anyone can see from outside.

The second is a list that SHIPS with the program. A catalogue file, an
official/community name list, a bundled thumbnail set: any of these makes the
launcher a directory of other people's games whether it fetches anything or
not.

The line the moderators drew is about what the program ships or fetches, not
about what a user adds themselves. So this gate reads the built package and
the source, and says nothing about what ends up in the user's plugin folder.

    python Tools/lint_no_game_index.py [--package <dir>]

Exit code 0 = clean, 1 = something to look at.
"""
import argparse
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Endpoints that answer "which games exist".
FORBIDDEN_URLS = [
    "archipelago.gg/datapackage",
    "archipelago.gg/api/datapackage",
]

# Symbols from the catalogue that must not come back.
FORBIDDEN_SYMBOLS = [
    "MergeWithOfficialApGamesAsync",
    "ApplyOfficialList",
    "class GameCatalog",
    "record CatalogEntry",
    "DefaultCatalogUrl",
]

# Filenames that are a game list by their nature.
FORBIDDEN_PACKAGE_FILES = [
    "catalog.json",
    "official_games.txt",
    "community_games.txt",
]

SOURCE_DIRS = ["Core", "UI", "Plugins"]
# Build output and package folders. Hidden directories (anything starting
# with a dot) are skipped separately, which covers version control and every
# editor or tool that keeps its state that way.
SKIP_DIRS = {"bin", "obj", "node_modules", "dist"}


def source_files():
    for d in SOURCE_DIRS:
        base = os.path.join(ROOT, d)
        if not os.path.isdir(base):
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [x for x in dirnames
                           if x not in SKIP_DIRS and not x.startswith(".")]
            for fn in filenames:
                if fn.endswith((".cs", ".xaml")):
                    yield os.path.join(dirpath, fn)


def rel(p):
    return os.path.relpath(p, ROOT).replace("\\", "/")


def check_source():
    problems = []
    for path in source_files():
        try:
            text = io.open(path, encoding="utf-8").read()
        except (IOError, UnicodeDecodeError):
            continue
        for i, line in enumerate(text.split("\n"), 1):
            stripped = line.strip()
            # A comment explaining why something was removed is not the thing
            # itself -- this gate should not fight its own documentation.
            if stripped.startswith("//") or stripped.startswith("<!--"):
                continue
            for url in FORBIDDEN_URLS:
                if url in line:
                    problems.append((rel(path), i, "henter spilliste: " + url))
            for sym in FORBIDDEN_SYMBOLS:
                if sym in line:
                    problems.append((rel(path), i, "katalogsymbol: " + sym))
    return problems


def check_package(pkg):
    problems = []
    if not os.path.isdir(pkg):
        return [("(pakke)", 0, "findes ikke: " + pkg)]
    for dirpath, dirnames, filenames in os.walk(pkg):
        # A folder of one thumbnail per game is a game list in picture form,
        # so the folder counts whether or not the text files are still in it.
        for dn in list(dirnames):
            if dn.lower() == "catalogrepo":
                full = os.path.join(dirpath, dn)
                problems.append((os.path.relpath(full, pkg).replace("\\", "/"),
                                 0, "katalogmappe ligger i pakken"))
                dirnames.remove(dn)
        for fn in filenames:
            if fn.lower() in FORBIDDEN_PACKAGE_FILES:
                full = os.path.join(dirpath, fn)
                problems.append((os.path.relpath(full, pkg).replace("\\", "/"),
                                 0, "spilliste ligger i pakken"))
    return problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--package", default=None,
                    help="mappe med den byggede pakke (springes over hvis udeladt)")
    args = ap.parse_args()

    print("lint_no_game_index")
    print("-" * 60)

    problems = check_source()
    print("kilde   : %d fil(er) laest, %d fund" % (
        sum(1 for _ in source_files()), len(problems)))

    if args.package:
        pkg = check_package(args.package)
        print("pakke   : %s, %d fund" % (args.package, len(pkg)))
        problems += pkg
    else:
        print("pakke   : sprunget over (--package ikke angivet)")

    if not problems:
        print()
        print("OK -- launcheren kender ingen spil.")
        return 0

    print()
    for path, line, why in problems:
        where = "%s:%d" % (path, line) if line else path
        print("  %-48s %s" % (where, why))
    print()
    print("FEJL: %d fund." % len(problems))
    return 1


if __name__ == "__main__":
    sys.exit(main())
