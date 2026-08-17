"""The launcher must never download an emulator.

An emulator is somebody else's software under somebody else's licence -- often
copyleft, and in snes9x's case a bespoke non-commercial one. Fetching it for
the player would make us the distributor, which is the exact category the
moderation case was about. The player installs their own copy into
Emulators/<backend>/ and the launcher only drives it.

This fails if the emulator code gains any way to fetch one.

    python Tools/lint_no_emulator_download.py
"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

# The files that are allowed to know about emulators at all.
#
# Core/Extensions is here from the day it was created. An emulator bridge now
# ships as an installable extension, and an extension that could fetch its own
# emulator would be the same distribution problem wearing a different hat --
# only harder to notice, because it would not live in the launcher's own repo.
TARGETS = [
    os.path.join(ROOT, "Plugins", "Emulated"),
    os.path.join(ROOT, "Core", "EmulatorBackends.cs"),
    os.path.join(ROOT, "Core", "Extensions"),
]

# Anything that could pull bytes down or unpack them once they are here.
BANNED = [
    (r"\bHttpClient\b",              "HttpClient"),
    (r"\bWebClient\b",               "WebClient"),
    (r"\bDownloadFile\w*\b",         "DownloadFile*"),
    (r"\bGetByteArrayAsync\b",       "GetByteArrayAsync"),
    (r"\bGetStreamAsync\b",          "GetStreamAsync"),
    (r"\bArchiveExtractor\b",        "ArchiveExtractor"),
    (r"\bZipFile\.ExtractToDirectory\b", "ZipFile.ExtractToDirectory"),
    (r"api\.github\.com",            "api.github.com"),
    (r"browser_download_url",        "browser_download_url"),
]


def cs_files():
    for target in TARGETS:
        if os.path.isfile(target):
            yield target
            continue
        for dirpath, dirnames, filenames in os.walk(target):
            dirnames[:] = [d for d in dirnames
                           if d not in ("bin", "obj") and not d.startswith(".")]
            for name in filenames:
                if name.endswith(".cs"):
                    yield os.path.join(dirpath, name)


def main():
    hits = []
    for path in cs_files():
        for n, line in enumerate(io.open(path, encoding="utf-8", errors="replace"), 1):
            if line.strip().startswith("//"):
                continue
            for pattern, label in BANNED:
                if re.search(pattern, line):
                    rel = os.path.relpath(path, ROOT)
                    hits.append((rel, n, label, line.strip()[:70]))

    if hits:
        print("The emulator code can fetch or unpack an archive:")
        for rel, n, label, text in hits:
            print("   %s:%d  [%s]  %s" % (rel, n, label, text))
        print()
        print("The launcher must not download emulators. The player installs")
        print("their own into Emulators/<backend>/; we only drive it.")
        return 1

    print("OK -- the launcher cannot download an emulator.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
