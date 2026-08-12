import json, re
from pathlib import Path

base = Path(__file__).parent.parent

# Get plugin GameIds
plugin_ids = set()
plugin_id_to_file = {}
for cs in (base / 'Plugins').rglob('*.cs'):
    text = cs.read_text(encoding='utf-8', errors='ignore')
    m = re.search(r'public(?:\s+override)?\s+string\s+GameId\s+=>\s+"([^"]+)"', text)
    if m:
        gid = m.group(1)
        plugin_ids.add(gid)
        plugin_id_to_file[gid] = cs.relative_to(base)

# Get catalog IDs
with open(base / 'CatalogRepo' / 'catalog.json', encoding='utf-8') as f:
    data = json.load(f)
catalog_ids = set(g['id'] for g in data['games'])
catalog_by_id = {g['id']: g for g in data['games']}

# Gaps
in_catalog_no_plugin = sorted(catalog_ids - plugin_ids)
in_plugin_no_catalog = sorted(plugin_ids - catalog_ids)

print(f"Plugin count:  {len(plugin_ids)}")
print(f"Catalog count: {len(catalog_ids)}")
print(f"Matched (both): {len(plugin_ids & catalog_ids)}")

print(f"\n=== In catalog but NO plugin ({len(in_catalog_no_plugin)}) ===")
# Sort by status then id
by_status = {}
for gid in in_catalog_no_plugin:
    g = catalog_by_id[gid]
    status = g.get('status', '?')
    by_status.setdefault(status, []).append(gid)

for status in sorted(by_status.keys()):
    entries = sorted(by_status[status])
    print(f"\n  -- status: {status!r} ({len(entries)} games) --")
    for gid in entries:
        g = catalog_by_id[gid]
        name = g.get('display_name', gid)
        print(f"    {gid:50s}  {name}")

print(f"\n=== Plugin but NO catalog entry ({len(in_plugin_no_catalog)}) ===")
for gid in in_plugin_no_catalog:
    print(f"  {gid:50s}  ({plugin_id_to_file.get(gid, '?')})")
