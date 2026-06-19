import json, time, urllib.request, sys

API = "http://127.0.0.1:8001"

wf = {
    "1": {"class_type": "UnetLoaderGGUF", "inputs": {"unet_name": "flux1-dev-Q8_0.gguf"}},
    "2": {"class_type": "DualCLIPLoader", "inputs": {
        "clip_name1": "clip_l_flux.safetensors",
        "clip_name2": "t5xxl_fp8_e4m3fn_scaled.safetensors",
        "type": "flux"}},
    "3": {"class_type": "VAELoader", "inputs": {"vae_name": "flux_ae.safetensors"}},
    "4": {"class_type": "CLIPTextEncode", "inputs": {
        "text": "a single glowing faceted emerald gemstone floating on a dark navy background, dramatic rim lighting, professional video game icon art, centered composition, clean, high detail",
        "clip": ["2", 0]}},
    "5": {"class_type": "EmptyLatentImage", "inputs": {"width": 768, "height": 768, "batch_size": 1}},
    "6": {"class_type": "KSampler", "inputs": {
        "seed": 42, "steps": 20, "cfg": 1.0,
        "sampler_name": "euler", "scheduler": "simple",
        "denoise": 1.0, "model": ["1", 0],
        "positive": ["4", 0], "negative": ["4", 0],
        "latent_image": ["5", 0]}},
    "7": {"class_type": "VAEDecode", "inputs": {"samples": ["6", 0], "vae": ["3", 0]}},
    "8": {"class_type": "SaveImage", "inputs": {"filename_prefix": "lv2_test", "images": ["7", 0]}},
}

req = urllib.request.Request(API + "/prompt",
    data=json.dumps({"prompt": wf}).encode(),
    headers={"Content-Type": "application/json"})
resp = json.load(urllib.request.urlopen(req, timeout=30))
pid = resp.get("prompt_id")
print("queued:", pid, flush=True)
if resp.get("node_errors"):
    print("NODE ERRORS:", json.dumps(resp["node_errors"])[:800]); sys.exit(1)

t0 = time.time()
while time.time() - t0 < 540:
    time.sleep(5)
    try:
        hist = json.load(urllib.request.urlopen(API + f"/history/{pid}", timeout=10))
    except Exception:
        continue
    if pid in hist:
        entry = hist[pid]
        status = entry.get("status", {})
        if status.get("status_str") == "error":
            msgs = [m for m in status.get("messages", []) if m[0] == "execution_error"]
            print("EXECUTION ERROR:", json.dumps(msgs)[:1200]); sys.exit(1)
        outputs = entry.get("outputs", {})
        if "8" in outputs and outputs["8"].get("images"):
            img = outputs["8"]["images"][0]
            print(f"DONE in {time.time()-t0:.0f}s -> {img['filename']}", flush=True)
            url = API + f"/view?filename={urllib.parse.quote(img['filename'])}&subfolder={urllib.parse.quote(img.get('subfolder',''))}&type={img.get('type','output')}"
            data = urllib.request.urlopen(url, timeout=30).read()
            out = r"C:\Users\marco\OneDrive\Desktop\Diablo II Archipelago\Launcher V2.0.0\Tools_AssetGen\ai_test.png"
            open(out, "wb").write(data)
            print("saved", len(data), "bytes ->", out)
            sys.exit(0)
print("TIMEOUT after 540s"); sys.exit(2)
