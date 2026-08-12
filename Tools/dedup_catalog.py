#!/usr/bin/env python3
"""
dedup_catalog.py — remove duplicate catalog.json entries.

When sync_catalog_plugins.py adds new entries with plugin GameIds,
old entries with different IDs for the same game (from community_games.txt)
remain. This script keeps the plugin-ID version and removes the old one.

Dedup key: ap_world_name. If two entries share an ap_world_name,
keep the one whose `id` is a known plugin GameId.
"""
import json, re
from collections import defaultdict
from datetime import date
from pathlib import Path

ROOT    = Path(__file__).parent.parent
CATALOG = ROOT / "CatalogRepo" / "catalog.json"

def main():
    # Build set of plugin GameIds from CS source
    plugin_ids: set[str] = set()
    for cs in (ROOT / "Plugins").rglob("*.cs"):
        text = cs.read_text(encoding="utf-8", errors="ignore")
        m = re.search(
            r'public(?:\s+override)?\s+string\s+GameId\s+=>\s+"([^"]+)"', text
        )
        if m:
            plugin_ids.add(m.group(1))

    print(f"Plugin IDs found: {len(plugin_ids)}")

    with open(CATALOG, encoding="utf-8") as f:
        data = json.load(f)

    games = data["games"]
    print(f"Before dedup: {len(games)} entries")

    # Group by ap_world_name (fall back to id if ap_world_name is missing/empty)
    by_world: dict[str, list] = defaultdict(list)
    for g in games:
        key = (g.get("ap_world_name") or "").strip() or g["id"]
        by_world[key].append(g)

    kept    = []
    removed = 0

    for world_name, entries in by_world.items():
        if len(entries) == 1:
            kept.append(entries[0])
            continue

        plugin_entries = [e for e in entries if e["id"] in plugin_ids]
        other_entries  = [e for e in entries if e["id"] not in plugin_ids]

        if plugin_entries:
            winner = plugin_entries[0]
            # Inherit description from old entry if new one is empty
            if not winner.get("description"):
                for old in other_entries:
                    if old.get("description"):
                        winner["description"] = old["description"]
                        break
            kept.append(winner)
            removed += len(other_entries) + len(plugin_entries) - 1
            if other_entries:
                print(
                    f"  DEDUP {world_name!r:40s}: "
                    f"kept {winner['id']!r}, "
                    f"dropped {[e['id'] for e in other_entries]}"
                )
        else:
            # No plugin match — keep the first (usually more complete)
            kept.append(entries[0])
            removed += len(entries) - 1

    print(f"After dedup: {len(kept)} entries (removed {removed} duplicates)")

    data["games"]   = kept
    data["updated"] = str(date.today())

    with open(CATALOG, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"Saved to {CATALOG}")

if __name__ == "__main__":
    main()
