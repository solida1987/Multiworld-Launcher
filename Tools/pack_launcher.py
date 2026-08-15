# -*- coding: utf-8 -*-
"""Build launcher_package.zip from the publish output, without the user's data.

The publish folder is a build directory that has also been RUN from, so it
accumulates runtime state next to the binaries: the installed-games list, the
settings file, crash logs. Zipping it wholesale ships one developer's state to
everyone, and because the self-update copies with `robocopy /E`, those files
land on top of what the user already had.

That is not hypothetical. `Data/library.json` went out in 2.9.32 carrying a
six-game library from 13 June; it was removed by hand for 2.9.33 and was back
in the publish tree by 2.9.36. Deleting it by hand is not a fix - remembering
is the part that fails. So the exclusion lives here, and the zip is verified
after it is written.

    python Tools/pack_launcher.py <publish-dir> <out.zip> [expected-version]
"""
import io
import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Runtime state. Matched on the archive path, case-insensitively.
EXCLUDE_PATHS = {
    "data/library.json",
    "data/seed_library.json",
    "launcher_settings.json",
    "crash.log",
    "self_test.log",
}
# Anything under these never belongs in a release.
EXCLUDE_DIRS = ("logs/",)
EXCLUDE_SUFFIXES = (".pdb", ".log")


def should_skip(rel):
    low = rel.replace("\\", "/").lower()
    if low in EXCLUDE_PATHS:
        return "brugertilstand"
    if low.endswith(EXCLUDE_SUFFIXES):
        return "debug/log"
    for d in EXCLUDE_DIRS:
        if low.startswith(d):
            return "log-mappe"
    return None


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    pub = os.path.abspath(sys.argv[1])
    out = os.path.abspath(sys.argv[2])
    expect_version = sys.argv[3] if len(sys.argv) > 3 else None

    if not os.path.isdir(pub):
        print("findes ikke: " + pub)
        return 2

    skipped = []
    written = 0
    if os.path.exists(out):
        os.remove(out)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for root, dirs, files in os.walk(pub):
            for fn in files:
                full = os.path.join(root, fn)
                rel = os.path.relpath(full, pub)
                why = should_skip(rel)
                if why:
                    skipped.append((rel, why))
                    continue
                z.write(full, rel)
                written += 1

    size_mb = os.path.getsize(out) / (1024.0 * 1024.0)
    print("skrev %s" % out)
    print("  %d filer, %.1f MB" % (written, size_mb))
    for rel, why in skipped:
        print("  udeladt: %-34s (%s)" % (rel, why))

    # ---- verify what actually landed in the zip -------------------------
    problems = []
    with zipfile.ZipFile(out) as z:
        names = z.namelist()
        lower = {n.replace("\\", "/").lower() for n in names}
        for bad in EXCLUDE_PATHS:
            if bad in lower:
                problems.append("zip indeholder stadig %s" % bad)
        exe = [n for n in names if n.lower().endswith("multiworld launcher.exe")]
        if not exe:
            problems.append("zip mangler Multiworld Launcher.exe")
        elif expect_version:
            data = z.read(exe[0])
            hits = (data.count(expect_version.encode("ascii"))
                    + data.count(expect_version.encode("utf-16-le")))
            if hits == 0:
                problems.append("exe indeholder ikke versionen %s" % expect_version)
            else:
                print("  version %s fundet %d gange i exe'en"
                      % (expect_version, hits))
    if size_mb < 50:
        problems.append("zip er kun %.1f MB — ser afkortet ud" % size_mb)

    # ---- every asset in the source tree must be in the zip ---------------
    #
    # A missing asset is invisible: the logo silently does not draw, an icon
    # falls back to the generic one, a sound does not play. Nothing errors,
    # nothing logs, and the first report is a screenshot with a blank space
    # in it. The build has skipped an asset copy at least once on this
    # machine without saying so, so the zip -- not the build -- is where
    # this gets checked.
    missing = []
    src_assets = os.path.join(ROOT, "Assets")
    if os.path.isdir(src_assets):
        for root, _dirs, files in os.walk(src_assets):
            for fn in files:
                rel = os.path.relpath(os.path.join(root, fn), ROOT)
                if rel.replace("\\", "/").lower() not in lower:
                    missing.append(rel)
    if missing:
        for m in sorted(missing)[:12]:
            problems.append("zip mangler asset: " + m)
        if len(missing) > 12:
            problems.append("...og %d asset(s) mere" % (len(missing) - 12))
    else:
        print("  alle %d assets er med"
              % sum(len(f) for _r, _d, f in os.walk(src_assets)))

    if problems:
        print()
        for p in problems:
            print("PROBLEM: " + p)
        return 1
    print("  zip verificeret")
    return 0


if __name__ == "__main__":
    sys.exit(main())
