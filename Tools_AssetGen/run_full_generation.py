"""
run_full_generation.py — autonomous generation chain for the launcher art set.

1. Waits for the ComfyUI queue to drain (jobs queued by an earlier run finish
   and land in history rather than being wasted).
2. Harvests every completed lv2_asset image from ComfyUI history: matches the
   prompt text back to (game, kind), post-processes and saves into Assets/.
3. Re-runs the first-party batch (skip-existing fills only the gaps).
4. Runs the popular-catalog thumbnail batch.

Run from the Tools_AssetGen directory or anywhere — paths are script-relative.
"""

import io
import json
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from generate_ai_assets import (  # noqa: E402
    API, ASSETS, CATALOG_THUMBS, GAMES, KINDS, PROMPT_KEY, STYLE_SUFFIX,
)
from PIL import Image  # noqa: E402


def queue_depth() -> int:
    q = json.load(urllib.request.urlopen(API + "/queue", timeout=10))
    return len(q.get("queue_running", [])) + len(q.get("queue_pending", []))


def wait_for_queue_drain(max_wait_s: int = 3600) -> None:
    t0 = time.time()
    while time.time() - t0 < max_wait_s:
        try:
            d = queue_depth()
        except Exception:
            d = -1
        if d == 0:
            print("queue drained", flush=True)
            return
        print(f"queue depth {d} — waiting...", flush=True)
        time.sleep(20)
    print("queue drain wait timed out — continuing anyway", flush=True)


def build_prompt_map() -> dict[str, tuple[str, str]]:
    """full prompt text -> (game_id, kind)"""
    m: dict[str, tuple[str, str]] = {}
    for gid, briefs in GAMES.items():
        for kind in ("icons", "heroes", "thumbs"):
            m[briefs[PROMPT_KEY[kind]] + STYLE_SUFFIX] = (gid, kind)
    for gid, prompt in CATALOG_THUMBS.items():
        m[prompt + STYLE_SUFFIX] = (gid, "thumbs")
    return m


def harvest() -> int:
    """Pull finished lv2_asset generations out of ComfyUI history into Assets/."""
    pmap = build_prompt_map()
    hist = json.load(urllib.request.urlopen(API + "/history?max_items=200", timeout=30))
    saved = 0
    for entry in hist.values():
        outputs = entry.get("outputs", {}).get("8", {})
        images = outputs.get("images") or []
        if not images:
            continue
        # Identify the job by its positive prompt text (node 4)
        try:
            text = entry["prompt"][2]["4"]["inputs"]["text"]
        except (KeyError, IndexError, TypeError):
            continue
        match = pmap.get(text)
        if not match:
            continue
        gid, kind = match
        _, _, post, path_fn = KINDS[kind]
        out_path = path_fn(gid)
        if out_path.exists() and out_path.stat().st_size > 20_000:
            continue  # already a real (non-placeholder) asset
        img_info = images[0]
        url = (API + "/view?filename=" + urllib.parse.quote(img_info["filename"]) +
               "&subfolder=" + urllib.parse.quote(img_info.get("subfolder", "")) +
               "&type=" + img_info.get("type", "output"))
        try:
            data = urllib.request.urlopen(url, timeout=60).read()
            img = Image.open(io.BytesIO(data)).convert("RGB")
            out_path.parent.mkdir(parents=True, exist_ok=True)
            post(img).save(out_path, "PNG", optimize=True)
            print(f"harvested {gid}/{kind} -> {out_path.name} "
                  f"({out_path.stat().st_size // 1024} KB)", flush=True)
            saved += 1
        except Exception as e:
            print(f"harvest failed for {gid}/{kind}: {e}", flush=True)
    print(f"harvest complete: {saved} assets recovered", flush=True)
    return saved


def run_script(*args: str) -> int:
    cmd = [sys.executable, str(Path(__file__).parent / "generate_ai_assets.py"), *args]
    print(">>", " ".join(cmd), flush=True)
    return subprocess.call(cmd)


def main() -> int:
    print("=== Phase 1: wait for in-flight ComfyUI jobs ===", flush=True)
    wait_for_queue_drain()

    print("=== Phase 2: harvest finished jobs from history ===", flush=True)
    harvest()

    print("=== Phase 3: first-party gaps (skip-existing) ===", flush=True)
    # NOTE: deliberately NOT --force — phase 2 results and any pre-existing
    # real art are kept; only missing/failed assets are generated.
    # The old procedural placeholders were already overwritten by --force
    # earlier or will be regenerated here because the harvest size-gate
    # treats >20KB files as real.
    rc1 = run_script()

    print("=== Phase 4: popular catalog thumbnails ===", flush=True)
    rc2 = run_script("--catalog-thumbs")

    print("=== Phase 5: final harvest sweep ===", flush=True)
    harvest()

    print(f"ALL DONE (first-party rc={rc1}, catalog rc={rc2})", flush=True)
    return 0 if (rc1 == 0 and rc2 == 0) else 1


if __name__ == "__main__":
    sys.exit(main())
