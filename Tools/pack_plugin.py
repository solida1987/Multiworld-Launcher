# -*- coding: utf-8 -*-
"""Build a .londonplugin from a plugin project's build output.

A plugin's bin\\ folder is not the package. Building against the launcher
copies the launcher's own assembly in beside the plugin -- Private=false does
not stop it for a WinExe reference -- and shipping that copy would be worse
than untidy: the plugin's IGamePlugin would come from a different assembly
than the host's, and the cast would fail with a message saying the type does
not implement an interface it visibly implements.

So the package is an explicit list, and anything that looks like a second copy
of the launcher is an error rather than a warning.

    python tools/pack_plugin.py Plugins/OpenTTD [-o dist]
"""
import argparse
import io
import json
import os
import sys
import zipfile

# Everything the loader reads. deps.json is optional: a plugin with no
# dependencies of its own does not need one.
REQUIRED = ["plugin.json"]

# A plugin that carries these would shadow the host's own types.
FORBIDDEN_PREFIXES = ["Multiworld Launcher", "LauncherV2"]


def build_output(project_dir):
    """The newest bin\\<config>\\<tfm>\\ under this project."""
    bin_dir = os.path.join(project_dir, "bin")
    if not os.path.isdir(bin_dir):
        return None
    candidates = []
    for config in os.listdir(bin_dir):
        cpath = os.path.join(bin_dir, config)
        if not os.path.isdir(cpath):
            continue
        for tfm in os.listdir(cpath):
            tpath = os.path.join(cpath, tfm)
            if os.path.isdir(tpath):
                candidates.append((os.path.getmtime(tpath), tpath))
    if not candidates:
        return None
    return max(candidates)[1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("project_dir")
    ap.add_argument("-o", "--out", default="dist")
    args = ap.parse_args()

    project = os.path.abspath(args.project_dir)
    manifest_path = os.path.join(project, "plugin.json")
    if not os.path.isfile(manifest_path):
        print("no plugin.json in " + project)
        return 1

    manifest = json.load(io.open(manifest_path, encoding="utf-8"))
    game_id = manifest.get("gameId", "")
    assembly = manifest.get("assembly", "")
    version = manifest.get("version", "0.0.0")
    if not game_id or not assembly:
        print("plugin.json needs gameId and assembly")
        return 1

    out_dir = build_output(project)
    if out_dir is None:
        print("no build output -- run dotnet build first")
        return 1

    # The assembly the manifest names, its dependency map, and the manifest.
    wanted = [assembly]
    deps = os.path.splitext(assembly)[0] + ".deps.json"
    if os.path.isfile(os.path.join(out_dir, deps)):
        wanted.append(deps)

    missing = [f for f in wanted + REQUIRED
               if not os.path.isfile(os.path.join(out_dir, f))
               and not os.path.isfile(os.path.join(project, f))]
    if missing:
        print("missing from the build output: " + ", ".join(missing))
        return 1

    for f in wanted:
        for bad in FORBIDDEN_PREFIXES:
            if os.path.basename(f).startswith(bad):
                print("refusing to package %s -- that is the launcher itself" % f)
                return 1

    os.makedirs(args.out, exist_ok=True)
    dst = os.path.join(args.out, "%s-%s.londonplugin" % (game_id, version))

    with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
        # plugin.json must sit at the top of the package; Inspect() looks there.
        z.write(manifest_path, "plugin.json")
        for f in wanted:
            src = os.path.join(out_dir, f)
            z.write(src, f)
            print("  %-28s %8d bytes" % (f, os.path.getsize(src)))

    print()
    print("%s  (%d bytes)" % (dst, os.path.getsize(dst)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
