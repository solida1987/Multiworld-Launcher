"""
generate_ai_assets.py — FLUX-powered game art pipeline for the Archipelago Launcher.

Drives a local ComfyUI instance (FLUX.1 Dev Q8 GGUF) to generate per-game
icons, hero banners and catalog thumbnails, then post-processes them with
Pillow to the exact sizes the launcher expects:

    Assets/<id>.png               256 x 256   (generated 1024x1024, Lanczos)
    Assets/Heroes/<id>_hero.png   1380 x 280  (generated 1344x576, scale+crop)
    Assets/Thumbs/<id>_thumb.png  488 x 256   (generated 1024x576, scale+crop)

All prompts are deliberately written around THEMES and MOODS — never named
characters, logos or trademarked symbols — so the output is original art
that evokes each game without reproducing protected material.

Resumable: existing output files are skipped (use --force to regenerate).

Usage:
    python generate_ai_assets.py                    # first-party games, all 3 asset kinds
    python generate_ai_assets.py --what icons       # only icons
    python generate_ai_assets.py --games alttp      # one game
    python generate_ai_assets.py --catalog-thumbs   # thumbnail batch for popular catalog games
    python generate_ai_assets.py --force            # regenerate even if files exist
"""

import argparse
import io
import json
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

from PIL import Image

# ── ComfyUI connection ─────────────────────────────────────────────────────────

API = "http://127.0.0.1:8001"   # ComfyUI desktop default port on this machine

ASSETS = Path(__file__).resolve().parent.parent / "Assets"

# FLUX settings (proven working on this box: 20 steps, cfg 1.0, euler/simple)
STEPS, CFG, SAMPLER, SCHEDULER = 20, 1.0, "euler", "simple"

STYLE_SUFFIX = (
    ", professional video game key art, rich color, dramatic cinematic lighting, "
    "high detail, clean composition, no text, no words, no letters, no logo, no watermark"
)

# ── First-party game briefs (theme-only, trademark-safe) ───────────────────────
# icon: square centered subject · hero: wide banner, LEFT side calm for UI text
# thumb: landscape showcase shot

GAMES = {
    "diablo2_archipelago": {
        "icon":  "a burning gothic stone archway engulfed in crimson flames with golden embers rising, dark fantasy dungeon entrance, ominous hellish glow, centered on near-black background",
        "hero":  "wide panoramic dark fantasy landscape, a ruined gothic cathedral burning with crimson fire on the right side, ember particles drifting, left half fading into near-black smoky darkness, ominous mood",
        "thumb": "a dark gothic dungeon corridor lit by braziers of crimson fire, gold treasure scattered on stone floor, dark fantasy action rpg atmosphere",
    },
    "openttd_archipelago": {
        "icon":  "a charming miniature steam locomotive on curved rails, isometric diorama style, tiny trees and signal lights, deep blue dusk background, soft warm lamp glow, centered",
        "hero":  "wide panoramic isometric miniature world of railways, a steam train crossing a viaduct over rolling green countryside at blue hour on the right side, left half fading into deep navy dusk sky, cozy simulation mood",
        "thumb": "isometric miniature transport network with trains, tiny stations and winding rails through hills at sunset, cozy diorama style",
    },
    "pokemon_emerald": {
        "icon":  "a glowing faceted emerald gemstone resting on lush tropical jungle leaves, adventure treasure mood, deep green background with soft light rays, centered",
        "hero":  "wide panoramic lush tropical region seen from above, dense jungle, a volcano on the horizon and ocean on the right side, a soft emerald glow over everything, left half fading into deep dark green, adventure mood",
        "thumb": "a vibrant tropical route with tall grass, a dirt path and distant volcano under warm sunlight, stylized adventure world",
    },
    "alttp": {
        "icon":  "an ornate sword embedded in a stone pedestal in a misty forest clearing, golden light rays through trees, classic fantasy adventure mood, centered",
        "hero":  "wide panoramic storybook fantasy kingdom, a castle on a hill and a mystical dark forest on the right side under golden afternoon light, left half fading into deep twilight blue, classic adventure mood",
        "thumb": "a top-down storybook fantasy landscape with hedges, a stone shrine and a winding path, bright saturated adventure colors",
    },
    "super_metroid": {
        "icon":  "a glowing orange energy sphere held in twisting alien organic roots, retro sci-fi cavern, teal bioluminescent accents, dark background, centered",
        "hero":  "wide panoramic alien planet cavern interior, glowing orange organic pods and teal crystal formations on the right side, mist and depth, left half fading into deep space black, lonely sci-fi exploration mood",
        "thumb": "a retro sci-fi alien cavern with glowing orange pods, teal crystals and a distant tunnel entrance, atmospheric exploration shot",
    },
    "_generic": {
        "icon":  "a small archipelago of floating islands connected by glowing bridges of light, seen from above on a dark teal sea, fantasy map style, centered",
        "hero":  "wide panoramic chain of mystical islands connected by arcs of golden light across a dark ocean at night on the right side, stars above, left half fading into near-black, wondrous mood",
        "thumb": "a fantasy archipelago of varied islands linked by glowing light bridges over a deep dark sea, view from high above",
    },
}

# ── Popular catalog games — thumbnails only (ids must match catalog.json) ──────

CATALOG_THUMBS = {
    "a_hat_in_time":          "a whimsical cartoon spaceship interior with colorful planets visible through a round window, cheerful 3d platformer mood",
    "a_short_hike":           "a cozy low-poly island mountain with a winding hiking trail, soft pastel sunset, peaceful indie game mood",
    "animal_well":            "a mysterious dark pixel-art well surrounded by strange glowing creatures and plants, enigmatic atmosphere",
    "against_the_storm":      "a fantasy settlement of tents and workshops in a rain-soaked dark forest, warm hearth lights against the storm",
    "another_crabs_treasure":  "a tiny hermit crab wearing a tin can shell on a sunlit ocean floor among coral, underwater adventure mood",
    "celeste":                "a snowy pixel-art mountain peak at dawn with northern lights, lonely climbing adventure mood",
    "dark_souls_iii":         "a desolate gothic kingdom under a pale dying sun, a bonfire with a coiled sword in the foreground, somber dark fantasy",
    "doom_1993":              "a brutalist sci-fi base corridor on a red rocky planet, hellish orange glow from a portal, retro shooter mood",
    "factorio":               "a sprawling top-down industrial factory complex with conveyor belts, pipes and smoke stacks on an alien planet, engineering mood",
    "hollow_knight":          "moody hand-drawn underground caverns with pale glowing flora, delicate insect wings drifting, melancholic blue palette",
    "kingdom_hearts_ii_final_mix": "a heart-shaped moon over a twilight city plaza, crossed keys motif made of light, dreamy fantasy mood",
    "lethal_company":         "an industrial moon facility at night seen through a scrappy spaceship window, eerie fog and warning lights, co-op horror mood",
    "minecraft":              "a blocky voxel landscape with cubic trees, a torch-lit cobblestone shelter at dusk, sandbox adventure mood",
    "the_legend_of_zelda_ocarina_of_time": "a small wind instrument resting on a mossy stone altar in a sacred forest temple, golden dust motes, nostalgic fantasy mood",
    "risk_of_rain_2":         "alien ruins on a stormy planet with cascading blue rain and floating monoliths, escalating danger mood",
    "rogue_legacy":           "a torch-lit stone castle gallery of family portraits, gold coins scattered, whimsical roguelite mood",
    "satisfactory":           "first-person view of a massive factory platform on a lush alien world, conveyor lines into the sunset, builder mood",
    "slay_the_spire":         "a towering spire of strange architecture rising through clouds, glowing cards swirling around its base, dark fantasy deckbuilder mood",
    "stardew_valley":         "a cozy pixel-art farm with crops, a wooden farmhouse and chickens at golden hour, wholesome farming mood",
    "subnautica":             "a vast alien ocean underwater scene with bioluminescent coral and a small one-person submarine, deep blue exploration mood",
    "super_mario_64":         "a bright castle courtyard with checkered towers and floating golden stars, joyful 3d platformer mood",
    "terraria":               "a 2d side-view pixel-art world cross-section: forest surface, underground caves with ore and lava below, sandbox adventure mood",
    "the_witness":            "a vivid uninhabited island with geometric colorful trees and a line-puzzle panel glowing among rocks, serene puzzle mood",
    "undertale":              "a lone heart of red light in a dark underground cavern, golden flowers growing toward it, bittersweet pixel mood",
}

# ── ComfyUI helpers ────────────────────────────────────────────────────────────

def build_workflow(prompt: str, width: int, height: int, seed: int) -> dict:
    full = prompt + STYLE_SUFFIX
    return {
        "1": {"class_type": "UnetLoaderGGUF", "inputs": {"unet_name": "flux1-dev-Q8_0.gguf"}},
        "2": {"class_type": "DualCLIPLoader", "inputs": {
            "clip_name1": "clip_l_flux.safetensors",
            "clip_name2": "t5xxl_fp8_e4m3fn_scaled.safetensors",
            "type": "flux"}},
        "3": {"class_type": "VAELoader", "inputs": {"vae_name": "flux_ae.safetensors"}},
        "4": {"class_type": "CLIPTextEncode", "inputs": {"text": full, "clip": ["2", 0]}},
        "5": {"class_type": "EmptyLatentImage", "inputs": {"width": width, "height": height, "batch_size": 1}},
        "6": {"class_type": "KSampler", "inputs": {
            "seed": seed, "steps": STEPS, "cfg": CFG,
            "sampler_name": SAMPLER, "scheduler": SCHEDULER,
            "denoise": 1.0, "model": ["1", 0],
            "positive": ["4", 0], "negative": ["4", 0],
            "latent_image": ["5", 0]}},
        "7": {"class_type": "VAEDecode", "inputs": {"samples": ["6", 0], "vae": ["3", 0]}},
        "8": {"class_type": "SaveImage", "inputs": {"filename_prefix": "lv2_asset", "images": ["7", 0]}},
    }


def generate(prompt: str, width: int, height: int, seed: int, timeout_s: int = 900) -> Image.Image:
    """Queue one generation, poll history, return the decoded PIL image."""
    req = urllib.request.Request(
        API + "/prompt",
        data=json.dumps({"prompt": build_workflow(prompt, width, height, seed)}).encode(),
        headers={"Content-Type": "application/json"})
    resp = json.load(urllib.request.urlopen(req, timeout=30))
    pid = resp["prompt_id"]
    if resp.get("node_errors"):
        raise RuntimeError(f"node errors: {json.dumps(resp['node_errors'])[:400]}")

    t0 = time.time()
    while time.time() - t0 < timeout_s:
        time.sleep(4)
        try:
            hist = json.load(urllib.request.urlopen(API + f"/history/{pid}", timeout=10))
        except Exception:
            continue
        if pid not in hist:
            continue
        entry = hist[pid]
        if entry.get("status", {}).get("status_str") == "error":
            msgs = [m for m in entry["status"].get("messages", []) if m[0] == "execution_error"]
            raise RuntimeError(f"execution error: {json.dumps(msgs)[:600]}")
        out = entry.get("outputs", {}).get("8", {})
        if out.get("images"):
            img = out["images"][0]
            url = (API + "/view?filename=" + urllib.parse.quote(img["filename"]) +
                   "&subfolder=" + urllib.parse.quote(img.get("subfolder", "")) +
                   "&type=" + img.get("type", "output"))
            data = urllib.request.urlopen(url, timeout=60).read()
            return Image.open(io.BytesIO(data)).convert("RGB")
    # Client-side timeout: interrupt the stuck job so it doesn't keep hogging
    # the ComfyUI queue and starve every job behind it (the failure mode that
    # burned the first batch).
    try:
        urllib.request.urlopen(
            urllib.request.Request(API + "/interrupt", data=b""), timeout=10)
    except Exception:
        pass
    raise TimeoutError(f"generation timed out after {timeout_s}s (job interrupted)")


# ── Post-processing (exact launcher sizes) ─────────────────────────────────────

def scale_cover_crop(img: Image.Image, tw: int, th: int) -> Image.Image:
    """Scale to cover tw x th, then center-crop (like CSS background-size: cover)."""
    scale = max(tw / img.width, th / img.height)
    nw, nh = round(img.width * scale), round(img.height * scale)
    img = img.resize((nw, nh), Image.LANCZOS)
    left, top = (nw - tw) // 2, (nh - th) // 2
    return img.crop((left, top, left + tw, top + th))


def save_png(img: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, "PNG", optimize=True)
    print(f"    -> {path.name} ({path.stat().st_size // 1024} KB)", flush=True)


# Asset kind: (gen W, gen H, post-process fn, output path fn)
# Icons generate at 768² — plenty for the 256² target and ~40% faster than
# 1024² on a 12GB card where the Q8 unet partially offloads to system RAM.
KINDS = {
    "icons":  (768, 768,   lambda im: im.resize((256, 256), Image.LANCZOS),
               lambda gid: ASSETS / f"{gid}.png"),
    # Heroes generate at 1152x480 (0.55MP) — measured on this 12GB card:
    # jobs at <=0.59MP run in ~85s, but >=0.77MP falls off a VRAM cliff into
    # driver sysmem-fallback (a 1344x576 job took 2 HOURS and starved the queue).
    "heroes": (1152, 480,  lambda im: scale_cover_crop(im, 1380, 280),
               lambda gid: ASSETS / "Heroes" / f"{gid}_hero.png"),
    "thumbs": (1024, 576,  lambda im: scale_cover_crop(im, 488, 256),
               lambda gid: ASSETS / "Thumbs" / f"{gid}_thumb.png"),
}

PROMPT_KEY = {"icons": "icon", "heroes": "hero", "thumbs": "thumb"}


def stable_seed(game_id: str, kind: str) -> int:
    return abs(hash((game_id, kind, "lv2"))) % (2**31)


def run_batch(jobs: list[tuple[str, str, str]], force: bool) -> tuple[int, int]:
    """jobs: (game_id, kind, prompt). Returns (done, failed)."""
    done = failed = 0
    for i, (gid, kind, prompt) in enumerate(jobs, 1):
        gw, gh, post, path_fn = KINDS[kind]
        out = path_fn(gid)
        if out.exists() and not force:
            print(f"[{i}/{len(jobs)}] {gid}/{kind}: exists, skip", flush=True)
            continue
        print(f"[{i}/{len(jobs)}] {gid}/{kind}: generating {gw}x{gh}...", flush=True)
        t0 = time.time()
        try:
            img = generate(prompt, gw, gh, stable_seed(gid, kind))
            save_png(post(img), out)
            print(f"    done in {time.time()-t0:.0f}s", flush=True)
            done += 1
        except Exception as e:
            print(f"    FAILED: {e}", flush=True)
            failed += 1
    return done, failed


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--what", default="all", choices=["all", "icons", "heroes", "thumbs"])
    ap.add_argument("--games", default="", help="comma-separated game ids (default: all first-party)")
    ap.add_argument("--catalog-thumbs", action="store_true",
                    help="generate the popular-catalog thumbnail batch instead")
    ap.add_argument("--force", action="store_true")
    args = ap.parse_args()

    # Sanity: ComfyUI reachable?
    try:
        urllib.request.urlopen(API + "/system_stats", timeout=5)
    except Exception:
        print(f"ComfyUI is not reachable on {API} — start the ComfyUI desktop app first.")
        return 2

    jobs: list[tuple[str, str, str]] = []
    if args.catalog_thumbs:
        for gid, prompt in CATALOG_THUMBS.items():
            jobs.append((gid, "thumbs", prompt))
    else:
        kinds = ["icons", "heroes", "thumbs"] if args.what == "all" else [args.what]
        ids = [g.strip() for g in args.games.split(",") if g.strip()] or list(GAMES)
        for gid in ids:
            if gid not in GAMES:
                print(f"unknown game id: {gid}"); return 2
            for kind in kinds:
                jobs.append((gid, kind, GAMES[gid][PROMPT_KEY[kind]]))

    print(f"{len(jobs)} jobs queued against {API}", flush=True)
    done, failed = run_batch(jobs, args.force)
    print(f"\nBatch complete: {done} generated, {failed} failed, "
          f"{len(jobs)-done-failed} skipped", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
