-- ═══════════════════════════════════════════════════════════════════════════════
-- bizhawk_ap_connector.lua — generic Archipelago bridge for BizHawk (EmuHawk).
--
-- Loaded by the Archipelago Launcher via:
--   EmuHawk.exe "<rom>" --lua="<this file>" --system=<sys>
--
-- WHAT IT DOES
-- ─────────────
--   1. Reads ap_config.json written by the launcher into the BizHawk folder.
--      Primary path comes from the AP_CONFIG_PATH environment variable set on
--      the EmuHawk process; the relative fallback ("ap_config.json") works
--      because the launcher sets BizHawk's working directory to that folder.
--   2. Loads the per-game module  <script_dir>/games/<lua_module>.lua
--      (both names come from ap_config.json; script_dir falls back to this
--      script's own directory via debug.getinfo).
--   3. Opens the launcher's named pipe PAIR with
--        io.open("\\\\.\\pipe\\<pipe_name>_c2s", "r+b")  -- tx: script → launcher
--        io.open("\\\\.\\pipe\\<pipe_name>_s2c", "r+b")  -- rx: launcher → script
--      Plain CRT byte-mode handles; the launcher's pipe servers run in BYTE
--      mode and every message is a newline-terminated text line. One pipe
--      per direction — see WHY TWO PIPES below.
--   4. Every emulated frame:
--        • module.poll()              → send "CHECK:<id1>,<id2>\n"   on tx
--        • module.is_goal_complete()  → send "GOAL\n" (once)         on tx
--        • send "SYNC\n" on tx, read ITEM lines + "SYNCEND\n" from rx,
--          and hand each received item to module.receive_item(id, meta).
--          ITEM lines come in two shapes:
--            "ITEM:<id>"                                  (legacy)
--            "ITEM:<id>|<index>|<player>|<flags>|<locId>" (extended)
--          The extended fields land in `meta` ({index=,player=,flags=,
--          location=}); index is the item's ABSOLUTE position in the
--          slot's AP item stream — save-tracking games (Pokémon Emerald)
--          need it for their received-count handshake. The launcher
--          replays the FULL stream from index 0 on every launch.
--
-- WHY TWO PIPES
-- ──────────────
--   A single duplex CRT stream ("r+b") obeys the ANSI update-mode rules:
--   fflush between write→read, a seek between read→write. A pipe is not
--   seekable, and the UCRT cannot complete the write→read handover without
--   one — the first read after a write returns an INSTANT EOF with the
--   stream error flag set and errno 0. That was exactly the v1 failure:
--   "connected to launcher" followed by "read failed" one frame later.
--   With one pipe per direction each FILE handle only ever moves one way,
--   so the direction rules never engage, on any CRT version. Both ends are
--   still opened "r+b": CRT "wb" implies create/truncate open dispositions
--   that named pipes reject, so the launcher creates both pipes duplex and
--   each side simply never uses one of the two directions.
--
-- WHY THE SYNC/SYNCEND HANDSHAKE
-- ───────────────────────────────
--   Pure Lua cannot poll a pipe for readability — file:read() on an empty
--   pipe blocks the whole emulator. So the connector never reads unsolicited
--   data: it asks ("SYNC") and the launcher always answers immediately
--   (queued "ITEM:" lines, then "SYNCEND") — even with zero items pending.
--   Reads therefore block only for one local round-trip (microseconds) —
--   effectively non-blocking.
--
-- DEBUG LOG
-- ──────────
--   Always on: ap_connector.log next to ap_config.json (the BizHawk folder),
--   truncated at every script start. Every state transition and every I/O
--   error lands there with the exact Lua error string and errno; per-frame
--   SYNC roundtrips are sampled (first 3, then every 100th) and a heartbeat
--   line proves the frame loop is alive every ~10 s. The launcher mirrors
--   the other end into bridge_trace.log when AP_BRIDGE_TRACE=1 is set.
--
-- GAME MODULE CONTRACT (games/<name>.lua must return a table M with):
--   M.init(ctx)                one-time setup. ctx = { config = <decoded
--                              ap_config.json>, json_decode = fn, log = fn }
--                              — modules may ignore it (legacy signature).
--   M.poll() -> { id, ... }    AP location ids newly checked since last call
--   M.is_goal_complete() -> bool
--   M.receive_item(item_id, meta)  apply one received AP item; meta is the
--                              extended-ITEM table described above (or nil
--                              for legacy plain ITEM lines)
--
-- DISCONNECT BEHAVIOUR
-- ─────────────────────
--   If the pipe drops (launcher closed), the connector marks itself
--   disconnected, stops sending, and keeps buffering new checks; the game
--   keeps running normally. The launcher's pipe is single-instance per
--   session, so no reconnect is attempted.
-- ═══════════════════════════════════════════════════════════════════════════════

-- ── Logging ───────────────────────────────────────────────────────────────────

-- File log (see DEBUG LOG in the header): ap_connector.log in the BizHawk
-- folder. Resolved from AP_CONFIG_PATH's directory so it lands next to
-- ap_config.json even if BizHawk's working directory is somewhere else;
-- falls back to the working directory. Truncated at every script start.
local log_file = nil
do
  local dir = ""
  local env_path = os.getenv and os.getenv("AP_CONFIG_PATH") or nil
  if env_path and env_path ~= "" then
    local d = env_path:match("^(.*[/\\])")
    if d then dir = d end
  end
  log_file = io.open(dir .. "ap_connector.log", "w")
end

-- File-only detail line (per-line flush so a crash never loses evidence).
local function flog(msg)
  if not log_file then return end
  local ok = pcall(function()
    log_file:write(os.date("%H:%M:%S"), " ", tostring(msg), "\n")
    log_file:flush()
  end)
  if not ok then log_file = nil end   -- file handle died — stop trying
end

local function log(msg)
  msg = tostring(msg)
  flog(msg)
  if console and console.log then
    pcall(console.log, "[AP] " .. msg)
  else
    print("[AP] " .. msg)
  end
end

local function toast(msg)
  if gui and gui.addmessage then pcall(gui.addmessage, "[AP] " .. tostring(msg)) end
  log(msg)
end

-- ── Minimal pure-Lua JSON decoder ─────────────────────────────────────────────
-- Decode-only: objects, arrays, strings (incl. \uXXXX + surrogate pairs),
-- numbers, true/false/null. Enough to parse the launcher's ap_config.json
-- (System.Text.Json output, which may \u-escape characters in paths).

local function utf8_encode(cp)
  if cp < 0x80 then
    return string.char(cp)
  elseif cp < 0x800 then
    return string.char(0xC0 + math.floor(cp / 0x40), 0x80 + cp % 0x40)
  elseif cp < 0x10000 then
    return string.char(0xE0 + math.floor(cp / 0x1000),
                       0x80 + math.floor(cp / 0x40) % 0x40,
                       0x80 + cp % 0x40)
  else
    return string.char(0xF0 + math.floor(cp / 0x40000),
                       0x80 + math.floor(cp / 0x1000) % 0x40,
                       0x80 + math.floor(cp / 0x40) % 0x40,
                       0x80 + cp % 0x40)
  end
end

local function json_decode(text)
  local pos = 1
  local parse_value  -- forward declaration

  local function fail(why)
    error(string.format("json: %s at byte %d", why, pos), 0)
  end

  local function skip_ws()
    local _, e = text:find("^[ \t\r\n]*", pos)
    pos = e + 1
  end

  local ESCAPES = { ['"'] = '"', ["\\"] = "\\", ["/"] = "/",
                    b = "\b", f = "\f", n = "\n", r = "\r", t = "\t" }

  local function parse_string()
    pos = pos + 1                               -- skip opening quote
    local out = {}
    while true do
      local c = text:sub(pos, pos)
      if c == "" then fail("unterminated string") end
      if c == '"' then pos = pos + 1; break end
      if c == "\\" then
        local esc = text:sub(pos + 1, pos + 1)
        if esc == "u" then
          local hex = text:sub(pos + 2, pos + 5)
          local cp  = (#hex == 4) and tonumber(hex, 16) or nil
          if not cp then fail("bad \\u escape") end
          pos = pos + 6
          -- combine UTF-16 surrogate pairs into one codepoint
          if cp >= 0xD800 and cp <= 0xDBFF and text:sub(pos, pos + 1) == "\\u" then
            local lo = tonumber(text:sub(pos + 2, pos + 5), 16)
            if lo and lo >= 0xDC00 and lo <= 0xDFFF then
              cp  = 0x10000 + (cp - 0xD800) * 0x400 + (lo - 0xDC00)
              pos = pos + 6
            end
          end
          out[#out + 1] = utf8_encode(cp)
        else
          local mapped = ESCAPES[esc]
          if not mapped then fail("bad escape '\\" .. esc .. "'") end
          out[#out + 1] = mapped
          pos = pos + 2
        end
      else
        out[#out + 1] = c
        pos = pos + 1
      end
    end
    return table.concat(out)
  end

  local function parse_number()
    local s = text:match("^-?%d+%.?%d*[eE]?[%+%-]?%d*", pos)
    local n = s and tonumber(s) or nil
    if not n then fail("bad number") end
    pos = pos + #s
    return n
  end

  local function parse_array()
    pos = pos + 1                               -- skip '['
    local arr = {}
    skip_ws()
    if text:sub(pos, pos) == "]" then pos = pos + 1; return arr end
    while true do
      arr[#arr + 1] = parse_value()
      skip_ws()
      local c = text:sub(pos, pos)
      if c == "]" then pos = pos + 1; return arr end
      if c ~= "," then fail("expected ',' or ']'") end
      pos = pos + 1
    end
  end

  local function parse_object()
    pos = pos + 1                               -- skip '{'
    local obj = {}
    skip_ws()
    if text:sub(pos, pos) == "}" then pos = pos + 1; return obj end
    while true do
      skip_ws()
      if text:sub(pos, pos) ~= '"' then fail("expected object key") end
      local key = parse_string()
      skip_ws()
      if text:sub(pos, pos) ~= ":" then fail("expected ':'") end
      pos = pos + 1
      obj[key] = parse_value()
      skip_ws()
      local c = text:sub(pos, pos)
      if c == "}" then pos = pos + 1; return obj end
      if c ~= "," then fail("expected ',' or '}'") end
      pos = pos + 1
    end
  end

  parse_value = function()
    skip_ws()
    local c = text:sub(pos, pos)
    if c == "{" then return parse_object() end
    if c == "[" then return parse_array()  end
    if c == '"' then return parse_string() end
    if c == "-" or c:match("^%d") then return parse_number() end
    if text:sub(pos, pos + 3) == "true"  then pos = pos + 4; return true  end
    if text:sub(pos, pos + 4) == "false" then pos = pos + 5; return false end
    if text:sub(pos, pos + 3) == "null"  then pos = pos + 4; return nil   end
    fail("unexpected character '" .. c .. "'")
  end

  local ok, result = pcall(parse_value)
  if not ok then return nil, result end
  return result
end

-- ── Config loading ────────────────────────────────────────────────────────────

local function script_dir_of_this_file()
  local ok, info = pcall(debug.getinfo, 1, "S")
  if ok and info and info.source then
    local src = info.source
    if src:sub(1, 1) == "@" then src = src:sub(2) end
    local dir = src:match("^(.*[/\\])")
    if dir then return dir end
  end
  return nil
end

local function read_all(path)
  local f = io.open(path, "rb")
  if not f then return nil end
  local data = f:read("*a")
  f:close()
  return data
end

local function load_config()
  local candidates = {}
  local env_path = os.getenv and os.getenv("AP_CONFIG_PATH") or nil
  if env_path and env_path ~= "" then candidates[#candidates + 1] = env_path end
  candidates[#candidates + 1] = "ap_config.json"  -- BizHawk CWD = emulator dir

  for _, path in ipairs(candidates) do
    local raw = read_all(path)
    if raw then
      local cfg, err = json_decode(raw)
      if type(cfg) == "table" then
        log("config loaded from " .. path)
        return cfg
      end
      log("config parse error in " .. path .. ": " .. tostring(err))
    end
  end
  return nil
end

-- ── Game module loading ───────────────────────────────────────────────────────

local function no_op_module(reason)
  log("using no-op game module (" .. reason .. ")")
  return {
    init             = function() end,
    poll             = function() return {} end,
    is_goal_complete = function() return false end,
    receive_item     = function(_) end,
  }
end

local function ensure_trailing_sep(dir)
  if dir == "" or dir:match("[/\\]$") then return dir end
  return dir .. "\\"
end

local function load_game_module(cfg)
  local name = cfg.lua_module
  if type(name) ~= "string" or name == "" then
    return no_op_module("no lua_module in ap_config.json")
  end

  -- Locate Plugins/Scripts/: prefer the launcher-provided absolute path
  -- (cfg.script_dir), fall back to this script's own directory.
  local base = cfg.script_dir
  if type(base) ~= "string" or base == "" then base = script_dir_of_this_file() end
  if not base then return no_op_module("cannot resolve script directory") end

  local path = ensure_trailing_sep(base) .. "games\\" .. name .. ".lua"
  local ok, mod = pcall(dofile, path)
  if not ok then
    log("failed to load game module " .. path .. ": " .. tostring(mod))
    return no_op_module("module load failed")
  end
  if type(mod) ~= "table" then
    return no_op_module("module did not return a table")
  end

  -- Fill any missing contract functions with safe no-ops so a partial module
  -- can never nil-crash the connector.
  if type(mod.init)             ~= "function" then mod.init             = function() end end
  if type(mod.poll)             ~= "function" then mod.poll             = function() return {} end end
  if type(mod.is_goal_complete) ~= "function" then mod.is_goal_complete = function() return false end end
  if type(mod.receive_item)     ~= "function" then mod.receive_item     = function(_) end end

  log("game module loaded: " .. name)
  return mod
end

-- ── Named pipes (CRT byte-mode client, one pipe per direction) ────────────────
-- See WHY TWO PIPES in the header: tx and rx are separate CRT handles so no
-- stream ever switches read/write direction (impossible on a non-seekable
-- pipe under the UCRT's update-mode rules).

local tx             = nil           -- "<pipe_name>_c2s": this script → launcher
local rx             = nil           -- "<pipe_name>_s2c": launcher → this script
local pipe_state     = "connecting"  -- connecting | connected | disconnected | disabled
local goal_sent      = false
local pending_checks = {}            -- ids waiting to be sent (buffered while offline)
local sent_ids       = {}            -- location id -> true (session-level dedup)
local item_queue     = {}            -- received item ids waiting to be applied
local sync_count     = 0             -- completed SYNC roundtrips (log sampling)

local CONNECT_TIMEOUT_SECS = 30
local connect_deadline     = os.time() + CONNECT_TIMEOUT_SECS
local connect_attempts     = 0

local function mark_disconnected(why)
  if tx then pcall(function() tx:close() end) end
  if rx then pcall(function() rx:close() end) end
  tx, rx = nil, nil
  if pipe_state ~= "disconnected" then
    pipe_state = "disconnected"
    flog("DISCONNECTED: " .. tostring(why))
    toast("disconnected from launcher (" .. tostring(why) .. ") — checks paused")
  end
end

local function try_connect(pipe_name)
  connect_attempts = connect_attempts + 1
  local t = io.open("\\\\.\\pipe\\" .. pipe_name .. "_c2s", "r+b")
  if not t then
    if connect_attempts == 1 or connect_attempts % 10 == 0 then
      flog("connect attempt #" .. connect_attempts .. ": launcher pipes not ready")
    end
    return false
  end
  local r = io.open("\\\\.\\pipe\\" .. pipe_name .. "_s2c", "r+b")
  if not r then
    t:close()
    flog("connect attempt #" .. connect_attempts .. ": _c2s opened but _s2c failed — retrying")
    return false
  end
  t:setvbuf("no")                    -- unbuffered: every write hits the pipe now
  r:setvbuf("no")
  tx, rx = t, r
  pipe_state = "connected"
  flog("CONNECTED: " .. pipe_name .. "_c2s + _s2c (attempt #" .. connect_attempts .. ")")
  toast("connected to launcher")
  return true
end

-- file:write/:flush do NOT raise on I/O errors — they return nil + message
-- + errno (the v1 pcall wrapper around them could never see a failure).
local function pipe_write(s)
  if pipe_state ~= "connected" or not tx then return false end
  local okv, err, code = tx:write(s)
  if okv then okv, err, code = tx:flush() end
  if not okv then
    flog(string.format("write failed: %s (errno %s)", tostring(err), tostring(code)))
    mark_disconnected("write failed")
    return false
  end
  return true
end

local function pipe_read_line()
  -- Blocking CRT read — only ever called right after a SYNC request, when
  -- the launcher is guaranteed to answer immediately (see file header).
  -- Returns the line, or nil + reason. file:read returns plain nil at EOF
  -- (pipe closed) and nil + message + errno on a real error — keep the two
  -- apart in the log: EOF means the launcher went away, an errno means a
  -- local CRT/OS failure.
  if not rx then return nil, "no pipe" end
  local line, err, code = rx:read("*l")
  if line == nil then
    if err == nil then return nil, "pipe closed (EOF)" end
    return nil, string.format("%s (errno %s)", tostring(err), tostring(code))
  end
  return (line:gsub("\r$", ""))
end

local function sync_poll()
  -- Request/response item poll. Reply format:
  --   zero or more "ITEM:<id>" lines, then exactly one "SYNCEND".
  if not pipe_write("SYNC\n") then return end
  local items_this_poll = 0
  local guard = 0
  while true do
    local line, why = pipe_read_line()
    if line == nil then
      flog("read failed during SYNC poll: " .. tostring(why))
      mark_disconnected("read failed — " .. tostring(why))
      return
    end
    if line == "SYNCEND" then
      sync_count = sync_count + 1
      if items_this_poll > 0 or sync_count <= 3 or sync_count % 100 == 0 then
        flog(string.format("sync roundtrip #%d ok (%d item(s))",
                           sync_count, items_this_poll))
      end
      return
    end
    -- Extended form first: "ITEM:<id>|<index>|<player>|<flags>|<locId>".
    local id, idx, player, flags, loc =
      line:match("^ITEM:(%-?%d+)|(%d+)|(%-?%d+)|(%-?%d+)|(%-?%d+)$")
    if not id then id = line:match("^ITEM:(%-?%d+)$") end
    if id then
      item_queue[#item_queue + 1] = {
        id       = tonumber(id),
        index    = idx    and tonumber(idx)    or nil,
        player   = player and tonumber(player) or nil,
        flags    = flags  and tonumber(flags)  or nil,
        location = loc    and tonumber(loc)    or nil,
      }
      items_this_poll = items_this_poll + 1
      flog("ITEM received: " .. id .. (idx and (" (index " .. idx .. ")") or ""))
    else
      flog("unexpected line in SYNC reply ignored: " .. line)
    end
    guard = guard + 1
    if guard > 4096 then
      flog("SYNC reply guard tripped (4096 lines) — protocol garbage?")
      return
    end
  end
end

-- ── Startup ───────────────────────────────────────────────────────────────────

if not (emu and emu.frameadvance) then
  log("not running inside EmuHawk — connector aborted")
  return
end

log("Archipelago BizHawk connector starting (two-pipe bridge)")

local cfg = load_config()
if not cfg then
  toast("ap_config.json not found/invalid — AP bridge disabled, game runs solo")
  cfg = {}
  pipe_state = "disabled"
elseif type(cfg.pipe_name) ~= "string" or cfg.pipe_name == "" then
  toast("no pipe_name in ap_config.json — AP bridge disabled, game runs solo")
  pipe_state = "disabled"
end

local game = load_game_module(cfg)
local ok_init, init_err = pcall(game.init, {
  config      = cfg,          -- decoded ap_config.json (slot_data, locations…)
  json_decode = json_decode,  -- so modules never need their own JSON parser
  log         = flog,         -- module lines land in ap_connector.log too
})
if not ok_init then log("game module init error: " .. tostring(init_err)) end

if cfg.slot then
  log(string.format("slot '%s'  game '%s'  server '%s'",
      tostring(cfg.slot), tostring(cfg.game), tostring(cfg.server)))
end

-- ── Main per-frame loop ───────────────────────────────────────────────────────

local frame = 0
while true do
  frame = frame + 1

  -- 1. Connect: retry every 30 frames (~0.5 s) for up to CONNECT_TIMEOUT_SECS.
  if pipe_state == "connecting" and frame % 30 == 0 then
    if not try_connect(cfg.pipe_name) and os.time() > connect_deadline then
      pipe_state = "disabled"
      toast("could not reach launcher pipe after " .. CONNECT_TIMEOUT_SECS
            .. "s — AP bridge disabled, game runs solo")
    end
  end

  -- 2. Ask the game module for newly-completed checks. Always runs (even while
  --    offline) so checks are buffered rather than lost.
  local ok_poll, new_checks = pcall(game.poll)
  if ok_poll and type(new_checks) == "table" then
    for _, id in ipairs(new_checks) do
      id = tonumber(id)
      if id and not sent_ids[id] then
        sent_ids[id] = true
        pending_checks[#pending_checks + 1] = id
      end
    end
  elseif not ok_poll and frame % 600 == 0 then
    log("game module poll error: " .. tostring(new_checks))
  end

  if pipe_state == "connected" then
    -- 3. Flush buffered checks.
    if #pending_checks > 0 then
      local payload = table.concat(pending_checks, ",")
      if pipe_write("CHECK:" .. payload .. "\n") then
        flog("CHECK sent: " .. payload)
        pending_checks = {}
      end
    end

    -- 4. Goal — send once.
    if not goal_sent then
      local ok_goal, done = pcall(game.is_goal_complete)
      if ok_goal and done == true then
        if pipe_write("GOAL\n") then
          goal_sent = true
          flog("GOAL sent")
          toast("goal complete — sent to Archipelago")
        end
      end
    end

    -- 5. Item poll (request/response — bounded read, see file header).
    if pipe_state == "connected" then sync_poll() end
  end

  -- 6. Apply received items to the game.
  while #item_queue > 0 do
    local entry = table.remove(item_queue, 1)
    local ok_item, item_err = pcall(game.receive_item, entry.id, entry)
    if not ok_item then log("receive_item error: " .. tostring(item_err)) end
  end

  -- 7. Heartbeat — proves the frame loop (and thus the emulator's message
  --    pump) is alive even when nothing else is being logged. ~10 s at 60fps.
  if frame % 600 == 0 then
    flog(string.format("heartbeat: frame %d  state=%s  syncs=%d  buffered_checks=%d",
                       frame, pipe_state, sync_count, #pending_checks))
  end

  emu.frameadvance()
end
