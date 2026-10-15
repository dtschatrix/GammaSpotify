-- Runs both game scripts with a fake engine UI layer, against the real daemon + mock Spotify.
local socket = require("socket")
local T0 = socket.gettime()
-- game time stands still while the game is paused; os.clock (real time on Windows) does not
local frozen_at, paused_total = nil, 0
function time_global() return frozen_at or (math.floor((socket.gettime() - T0) * 1000) - paused_total) end
function pause_game(on)
	if on then frozen_at = time_global() else paused_total = paused_total + (math.floor((socket.gettime() - T0) * 1000) - paused_total - frozen_at); frozen_at = nil end
end
os.clock = function() return socket.gettime() - T0 end
EFF = 0.3
function get_console() return { get_float = function(_, name) assert(name == "snd_volume_eff"); return EFF end } end
snd_type = { world_ambient = 134217856 }
function printf(fmt, ...) print(string.format(fmt, ...)) end
DIK_keys = { DIK_ESCAPE = 1 }
db = { actor = {} }
actor_menu = { set_msg = function(_, msg) print("HUD> " .. msg) end }
ui_events = { BUTTON_CLICKED = 17, EDIT_TEXT_COMMIT = 71, WINDOW_KEY_PRESSED = 10 }
local cbs = {}
function RegisterScriptCallback(name, fn) cbs[name] = cbs[name] or {}; table.insert(cbs[name], fn) end
function Register_UI() end
function Unregister_UI() end
function vector()
	return {
		set = function(self, x, y, z) self.x, self.y, self.z = x, y, z; return self end,
		distance_to = function(self, o) return math.sqrt((self.x - o.x) ^ 2 + (self.y - o.y) ^ 2 + (self.z - o.z) ^ 2) end,
	}
end
function vector2() return { set = function(self, x, y) self.x, self.y = x, y; return self end } end
local IPC = os.getenv("IPC_DIR")
local GAMEDATA = IPC:gsub("[^/]+$", "") .. "gamedata/"
local LEVEL_DIR, SND_ROOT = GAMEDATA .. "levels/testlevel/", GAMEDATA .. "sounds/"
local function unix(p) return (p:gsub("\\", "/")) end
local real_open = io.open
io.open = function(path, mode) return real_open(unix(path), mode) end   -- Windows accepts both slashes
local index, flagged = {}, false       -- index = the engine's in-memory file list
STARTED = {}
function getFS()
	return {
		update_path = function(_, alias, name)
			if alias == "$level$" then return LEVEL_DIR .. name end
			if alias == "$game_sounds$" then return SND_ROOT .. name end
			assert(alias == "$app_data_root$"); return IPC .. "/" .. name
		end,
		path_exist = function() error("not needed") end,
		append_path = function() error("returns an unregistered engine type: trips the unstable-state screen") end,
		get_path = function() error("returns an unregistered engine type: trips the unstable-state screen") end,
		rescan_path = function(_, alias) assert(alias == "$level$", "rescan of an unknown alias would crash the game"); flagged = true end,
		rescan_pathes = function()
			assert(flagged); flagged = false
			for f in io.popen('ls "' .. LEVEL_DIR .. '" 2>/dev/null'):lines() do index[LEVEL_DIR .. f] = true end
		end,
		exist = function(_, alias, name)
			local root = alias == "$level$" and LEVEL_DIR or alias == "$game_sounds$" and SND_ROOT or error("alias " .. alias)
			return index[unix(root .. name)] and {} or nil
		end,
		file_delete = function(_, path) path = unix(path); index[path] = nil; os.remove(path) end,
		file_list_open = function(_, alias, sub, flags)
			assert(alias == "$game_sounds$" and flags == 9)
			local names = {}
			for f in io.popen('ls "' .. unix(SND_ROOT .. sub) .. '" 2>/dev/null'):lines() do names[#names + 1] = f; index[unix(SND_ROOT .. sub) .. f] = true end
			return { Size = function() return #names end, GetAt = function(_, i) return names[i + 1] end }
		end,
		file_rename = function(_, src, dst, overwrite)
			src, dst = unix(src), unix(dst)
			if not index[src] then return end
			index[src] = nil; index[dst] = true
			os.execute('mkdir -p "' .. dst:gsub("[^/]+$", "") .. '"'); assert(os.rename(src, dst))
		end,
	}
end
FS = { FS_ListFiles = 1, FS_RootOnly = 8 }
function bit_or(a, b) return a + b end
SOUNDS = {}        -- every sound created; each records what the engine was asked to do
sound_object = setmetatable({ s2d = 2, s3d = 0 }, { __call = function(_, path, stype)
	-- like the engine: only indexed files under gamedata\sounds load; anything else is the 576 ms silent placeholder
	local function real(p) local full = unix(SND_ROOT .. p .. ".ogg"); return index[full] and io.open(full, "rb") ~= nil end
	local o = { path = path, parts = { path }, ok = real(path), hop = HOP or 5000, stype = stype, volume = 1 }
	SOUNDS[#SOUNDS + 1] = o
	local function keep(p)   -- keep a copy of what the engine was given, for the audio check afterwards
		if KEEP then KEPT = (KEPT or 0) + 1; os.execute(string.format('cp "%s" "%s/%05d.ogg"', unix(SND_ROOT .. p .. ".ogg"), KEEP, KEPT)) end
		if KEEP2 then os.execute(string.format('cp "%s" "%s/"', unix(SND_ROOT .. p .. ".ogg"), KEEP2)) end
	end
	if o.ok then keep(path) end
	function o:length() return self.ok and #self.parts * self.hop or 576 end
	local function begin(self, kind) assert(self.ok); self.kind = kind; self.at = time_global(); STARTED[#STARTED + 1] = { path = path, at = self.at, kind = kind } end
	function o:play_at_pos(obj, p, delay, flags) assert(p.x and flags == 0); self.owner = obj; self.pos = p; self.max_distance = 300; begin(self, "3d") end
	function o:set_position(p) assert(self.kind == "3d" and p.x); self.pos = p end
	-- Like the engine: a sound that is too quiet or (3D) out of its range is no longer rendered, only
	-- simulated: nothing is read ahead and files are switched late (here: 200 ms behind the clock).
	-- Picking a chained sound up again after that crashes the real engine, so it must never be seen.
	function o:culled()
		if not self.at or self.stopped then return false end
		if self.kind == "2d" then return EFF * self.volume < 0.01 end
		local d = ACTOR_POS:distance_to(self.pos)
		local occluded = self.stype ~= snd_type.world_ambient and WALL and 0.1 or 1
		return d > self.max_distance or math.min(self.volume, (self.max_distance - d) / (self.max_distance - self.min_distance)) * EFF * occluded < 0.01
	end
	function o:play(obj, delay, flags) assert(flags == 2); begin(self, "2d") end
	function o:playing() return self.at ~= nil and not self.stopped and (self.heard or 0) < self:length() end
	function o:stop() self.stopped = true end
	-- The engine's streamer, as in xrSound: three OpenAL buffers of 400 or 800 ms (a global that the
	-- last started sound sets), refilled as they finish; reading moves a cursor through the chained files.
	function o:read_block()
		local total, b = self:length(), BLOCK()
		if self.cursor + b > total or self.cursor >= total then self.hit_end = true; SILENCES = SILENCES + 1 end
		self.cursor = self.cursor + b
		self.q[#self.q + 1] = b
	end
	function o:engine_update(now)
		if not self.at or self.stopped then return end
		if not self.cursor then self.cursor, self.q, self.q_start, self.heard, self.last = 0, {}, 0, 0, now; for _ = 1, 3 do self:read_block() end end
		self.heard = self.heard + (now - self.last) * (self.kind == "3d" and DOPPLER or 1); self.last = now
		while self.q[1] and self.heard >= self.q_start + self.q[1] and self.heard < self:length() do
			self.q_start = self.q_start + table.remove(self.q, 1); self:read_block()
		end
	end
	function o:attach_tail(p)
		assert(real(p), "tail attached for a file the engine cannot open: " .. p)
		-- the file being read: the one the cursor is in (a cursor exactly on a boundary has moved on)
		local reading = self.cursor and math.floor(self.cursor / self.hop) + 1 or 1
		if #self.parts - reading >= 2 then REFUSED = REFUSED + 1; return end   -- two tails queued: silently refused
		if self.cursor and self.cursor >= self:length() then FREEZES = FREEZES + 1 end   -- the real engine loops forever here
		-- the engine opens the files it holds later on: they must still be on disk
		for i = math.min(reading, #self.parts), #self.parts do assert(real(self.parts[i]), "file deleted while the engine still needs it: " .. self.parts[i]) end
		self.parts[#self.parts + 1] = p
		keep(p)
	end
	return o
end })
ACTOR_POS = vector():set(10, 0, -5)
REFUSED, FREEZES, SILENCES, CULLED, WALL, DOPPLER = 0, 0, 0, 0, false, 1
local block_model = os.getenv("FAKE_BLOCK") or "alt"
function BLOCK() if block_model == "alt" then return (math.floor(time_global() / 2300) % 2 == 0) and 800 or 400 end return tonumber(block_model) end
db.actor.position = function() return vector():set(ACTOR_POS.x, ACTOR_POS.y, ACTOR_POS.z) end
function Frect() return { set = function(self) return self end } end

-- valid node paths from the XML
local xml_path = arg[1] .. "/configs/ui/ui_spotify_radio.xml"
local nodes, depth, top = {}, 0, nil
for close, name, selfclose in io.open(xml_path):read("*a"):gsub("<!%-%-.-%-%->", ""):gmatch("<(/?)([%w_]+)[^>]-(/?)>") do
	if close == "/" then depth = depth - 1
	else
		if depth == 1 then top = name; nodes[name] = true elseif depth == 2 then nodes[top .. ":" .. name] = true end
		if selfclose ~= "/" then depth = depth + 1 end
	end
end

local function control(kind, node)
	local c = { kind = kind, node = node, shown = true, text = "" }
	local tc = { SetText = function(_, t) assert(type(t) == "string", "SetText needs a string"); c.text = t end, GetText = function() return c.text end }
	function c:TextControl() return tc end
	function c:Show(b) assert(type(b) == "boolean", "Show needs a boolean"); self.shown = b end
	function c:SetWndPos(v) assert(v.x and v.y); self.pos = v end
	function c:SetText(t) self.text = t end
	function c:GetText() return self.text end
	function c:CaptureFocus(b)
		if b then
			local o = self._owner
			if o._capturer and o._capturer.focus then
				o._capturer.focus = false
				local cb = o._cb[o._capturer._name .. ui_events.EDIT_TEXT_COMMIT]
				if cb then cb() end
			end
			o._capturer = self
		end
		self.focus = b
	end
	return c
end
function CScriptXmlInit()
	local x = {}
	function x:ParseFile(f) assert(f == "ui_spotify_radio.xml") end
	for _, m in ipairs({ "InitStatic", "InitFrame", "Init3tButton", "InitEditBox" }) do
		x[m] = function(_, node, parent) assert(nodes[node], "XML node missing: " .. node); assert(parent, "no parent for " .. node); return control(m, node) end
	end
	return x
end
CUIScriptWnd = {}
function CUIScriptWnd:SetWndRect() end
function CUIScriptWnd:SetAutoDelete() end
function CUIScriptWnd:Register(ctrl, name) assert(ctrl and name); self._ctrl = self._ctrl or {}; self._ctrl[name] = ctrl; ctrl._owner = self; ctrl._name = name end
function CUIScriptWnd:AddCallback(name, ev, fn, obj) assert(self._ctrl[name], "callback for unregistered control " .. name); assert(type(fn) == "function"); self._cb = self._cb or {}; self._cb[name .. ev] = function() fn(obj) end end
function CUIScriptWnd:Update() end
function CUIScriptWnd:OnKeyboard() return false end
function CUIScriptWnd:IsShown() return self._shown == true end
function CUIScriptWnd:ShowDialog() self._shown = true end
function CUIScriptWnd:HideDialog() self._shown = false end
function class(name)
	return function(base)
		local cls = setmetatable({}, { __index = base })
		getmetatable(cls).__call = function(_, ...)
			local o = setmetatable({}, { __index = cls })
			super = function() end
			cls.__init(o, ...)
			return o
		end
		_G[name] = cls
	end
end

local function load_script(name, as_shipped)
	local env = setmetatable({}, { __index = _G })
	local src = io.open(arg[1] .. "/scripts/" .. name .. ".script"):read("*a")
	if arg[2] == "socket" then src = src:gsub("local PREFER_SOCKET = false", "local PREFER_SOCKET = true") end
	if name == "spotify_radio_stream" and not as_shipped then
		-- As shipped, in-game audio starts by itself and world mode is disabled. For most of the
		-- tests the stream is switched by hand and world mode is enabled.
		local a, b
		src, a = src:gsub("local IN_GAME_AUDIO = true", "local IN_GAME_AUDIO = false")
		src, b = src:gsub("local WORLD_ENABLED = false", "local WORLD_ENABLED = true")
		assert(a == 1 and b == 1, "switches not found in spotify_radio_stream.script")
	end
	local chunk = assert(loadstring(src, name))
	setfenv(chunk, env); chunk(); _G[name] = env
	return env
end
pda = { set_active_subdialog = function(section) return "vanilla:" .. section end }
local radio = load_script("spotify_radio")
local stream = load_script("spotify_radio_stream")
local ui_mod = load_script("spotify_radio_ui")
radio.on_game_start(); stream.on_game_start(); ui_mod.on_game_start()

local tab
local function run(ms)
	local stop = socket.gettime() + ms / 1000
	while socket.gettime() < stop do
		if not frozen_at then for _, f in ipairs(cbs.actor_on_update) do f() end end
		-- the engine's own sound update comes after the scripts in a frame
		local last = SOUNDS and SOUNDS[#SOUNDS]
		if last and not frozen_at then last:engine_update(time_global()) end
		if last and last:culled() and last:playing() then CULLED = CULLED + 1 end
		if tab then tab:Update() end
		socket.sleep(0.01)
	end
end
local function click(wnd, name) assert(wnd._cb[name .. ui_events.BUTTON_CLICKED], "no click handler: " .. name)() end
local fails = 0
local function check(cond, what) print((cond and "PASS " or "FAIL ") .. what); if not cond then fails = fails + 1 end end

check(pda.set_active_subdialog("eptTasks") == "vanilla:eptTasks", "other PDA tabs untouched")
tab = pda.set_active_subdialog("eptRadio")
check(type(tab) == "table" and tab == pda.set_active_subdialog("eptRadio"), "radio tab returns the Spotify window (singleton)")
-- another PDA mod wraps the function after us and handles the radio tab itself
local ours = pda.set_active_subdialog
pda.set_active_subdialog = function(section) if section == "eptRadio" then return "other mod" end return ours(section) end
check(pda.set_active_subdialog("eptRadio") == "other mod", "(another PDA mod takes the radio tab)")
for _ = 1, 3 do for _, f in ipairs(cbs.actor_on_update) do f() end end
check(pda.set_active_subdialog("eptRadio") == tab and pda.set_active_subdialog("eptTasks") == "vanilla:eptTasks", "the Spotify tab is put back on top; other tabs still go through both mods")
check(cbs.on_key_press == nil or true, "loaded")
run(700)
for _, f in ipairs(cbs.actor_on_first_update or {}) do f() end   -- as on loading a save
check(radio.is_connected(), "opening the tab connects via " .. radio.link_mode)
check(radio.link_mode == (arg[2] or "file"), "link mode")
print("   now: " .. tab.now_playing:GetText() .. " | " .. tab.status:GetText() .. " | " .. tab.list_title:GetText())
check(tab.btn_play.shown and not tab.btn_pause.shown, "play button visible while idle")
check(not tab.rows[1].st.shown and not tab.btn_page_next.shown, "rows hidden before any search")

click(tab, "btn_play"); run(1500)
check(tab.now_playing:GetText() == "Burial - Archangel", "now playing: " .. tab.now_playing:GetText())
check(tab.btn_pause.shown and not tab.btn_play.shown, "pause button visible while playing")
print("   status: " .. tab.status:GetText())
check(tab.status:GetText():find("0:1%d / 3:58") and tab.status:GetText():find("vol 50%%"), "status line has time and volume")

click(tab, "btn_search")
local dlg; for i = 1, 10 do local n, v = debug.getupvalue(ui_mod.open_search, i); if n == "SEARCH" then dlg = v end end
check(dlg and dlg:IsShown(), "search dialog opens")
dlg.input:SetText("  burial untrue ")
dlg._cb["fld_input" .. ui_events.EDIT_TEXT_COMMIT](); run(800)
check(not dlg:IsShown(), "enter closes the dialog")
check(tab.rows[1].st.shown and tab.rows[1].text:GetText() == "[track] Archangel - Burial", "row 1: " .. tab.rows[1].text:GetText())
check(tab.rows[3].text:GetText() == "[playlist] Dark Ambient - someone" and not tab.rows[3].queue.shown and tab.rows[3].play.shown, "playlist row has play but no queue")
check(tab.rows[1].queue.shown and not tab.rows[5].st.shown, "track row has queue; unused rows hidden")
check(tab.list_title:GetText() == "burial untrue  (4)", "list title: " .. tab.list_title:GetText())
check(tab.rows[2].st.pos.y == 278 + 38 and tab.rows[2].play.pos.x == 622, "rows are laid out")

click(tab, "row_play_2"); run(1500)
check(radio.state.uri == "spotify:track:2", "row play starts that track")
click(tab, "row_queue_1"); click(tab, "btn_shuffle"); click(tab, "btn_vol_down"); run(1500)
check(radio.state.shuffle == true and radio.state.volume == 40, "shuffle + volume buttons: " .. tab.status:GetText())
click(tab, "btn_next"); click(tab, "btn_pause"); run(1500)
check(radio.state.status == "paused" and tab.status:GetText():find("paused"), "pause")
click(tab, "btn_playlists"); run(800)
check(tab.rows[1].text:GetText() == "[playlist] Zone walk - me" and not tab.rows[2].st.shown, "my playlists listed")

-- search confirmed with the button (focus stays in the box), then opened again
click(tab, "btn_search"); dlg.input:SetText("untrue"); click(dlg, "btn_ok"); run(800)
check(not dlg:IsShown() and tab.list_title:GetText() == "untrue  (4)", "search via the button works")
click(tab, "btn_search")
check(dlg:IsShown(), "search dialog opens again after a button-confirmed search")
click(dlg, "btn_cancel"); click(tab, "btn_search")
check(dlg:IsShown(), "and after cancel")
dlg.input:SetText("burial"); dlg._cb["fld_input" .. ui_events.EDIT_TEXT_COMMIT](); run(800)
check(not dlg:IsShown() and tab.list_title:GetText() == "burial  (4)", "enter still searches")

radio.search("\202\232\237\238 abc"); run(800)   -- "Кино abc" as cp1251 bytes, the way the game sends it
local q = io.popen("curl -s http://127.0.0.1:47900/_query"):read("*a")
check(q:find("\\u041a\\u0438\\u043d\\u043e abc", 1, true), "Cyrillic query reaches Spotify intact: " .. q)

-- Spotify through the engine: the daemon turns its PCM source into chunks, the game chains them into one sound
local played_dir = unix(SND_ROOT) .. "spotify_radio/"
local function count_files() local n = 0; for _ in io.popen('ls "' .. played_dir .. '" 2>/dev/null'):lines() do n = n + 1 end; return n end
local function api(path) return io.popen("curl -s 'http://127.0.0.1:47900" .. path .. "'"):read("*a") end
local function phone(act, ms) api("/_do?act=" .. act .. (ms and ("&ms=" .. ms) or "")) end   -- done in another Spotify app
local function spotify(key) return api("/_state"):match('"' .. key .. '": ([%w]+)') end
local function wait_for(pred, ms)
	local stop = socket.gettime() + ms / 1000
	while socket.gettime() < stop do if pred() then return true end; run(30) end
	return pred()
end
local function chunk_no(p) return tonumber(p:match("(%d+)$")) end
local function gapless(o) for i = 2, #o.parts do if chunk_no(o.parts[i]) ~= chunk_no(o.parts[i - 1]) + 1 then return false end end return true end
local function newest() return SOUNDS[#SOUNDS] end
-- the chunk the fake engine is playing right now
local function heard_chunk(o) return chunk_no(o.parts[math.min(#o.parts, math.floor(o.heard / 1000) + 1)]) end
-- waits for a sound other than "old" to play; returns it and how long that took
local function next_sound(old, ms)
	local t = socket.gettime()
	local ok = wait_for(function() return newest() ~= old and newest():playing() end, ms)
	return ok and newest() or nil, socket.gettime() - t
end
HOP = 1000
KEEP = os.getenv("KEEP_DIR"); os.execute('rm -rf "' .. KEEP .. '"; mkdir -p "' .. KEEP .. '"')
phone("play"); run(1200)
check(tab.btn_stream == nil and stream.get_mode() == "off", "no stream switch in the PDA; nothing streams while disabled")
local t0, n, world_parts = socket.gettime(), 0, 0
if not os.getenv("QUICK") then   -- QUICK=1 skips the world-mode part while working on the rest
stream.set_mode("world")
check(wait_for(function() return #STARTED >= 1 end, 6000) and stream.get_mode() == "world",
	string.format("world mode (enabled for the tests only): heard %.1f s after asking", socket.gettime() - t0))
run(15000)
local snd = newest()
check(api("/_state"):find('"device": "pda"'), "playback was moved to the game's Spotify device")
check(#STARTED == 1 and snd.kind == "3d", "one continuous 3D sound, started once (starts: " .. #STARTED .. ")")
check(#snd.parts >= 13 and #snd.parts <= 20 and snd:playing(), "chunks are chained on as the engine takes them: " .. #snd.parts .. " in 15 s")
check(FREEZES == 0 and SILENCES == 0 and REFUSED > 0, "the engine never read to the end of its data (silences " .. SILENCES .. ", freezes " .. FREEZES .. ", refused offers " .. REFUSED .. ")")
check(stream.lag_ms() >= 4000 and stream.lag_ms() <= 9000, "about six seconds of audio are held: " .. stream.lag_ms() .. " ms")
check(count_files() <= 14, "finished chunk files are deleted as it goes, " .. count_files() .. " on disk")
local starts0 = #STARTED
world_parts = #snd.parts
KEEP = nil

-- world mode: distance and walls. The engine must never get to cull the playing sound.
check(snd.stype == snd_type.world_ambient and snd.owner == nil and snd.max_distance >= 500, "world sound is un-occluded, ownerless and has a huge engine range")
check(math.abs(snd.volume - 1) < 0.05, "full volume next to the radio: " .. snd.volume)
WALL = true; ACTOR_POS:set(10 + 23.5, 0, -5); run(1500)
check(math.abs(snd.volume - 0.5) < 0.02, "half volume half way out: " .. snd.volume)
ACTOR_POS:set(10 + 44, 0, -5); run(2500)
check(#STARTED == starts0 and snd:playing() and snd.volume * EFF >= 0.02 and snd.volume < 0.12, "at the edge of earshot the same sound keeps running, quiet but not cullable: " .. snd.volume)
check(gapless(snd) and #snd.parts >= world_parts + 3, "and is still chained without a gap (" .. #snd.parts .. " chunks)")
ACTOR_POS:set(10 + 100, 0, -5); run(4000)     -- far out of range
check(snd.stopped and #STARTED == starts0, "out of earshot the sound is stopped and none is started")
check(count_files() <= 8, "out of earshot only the newest chunks are kept, " .. count_files() .. " on disk")
ACTOR_POS:set(10 + 5, 0, -5); run(700)
local snd2 = newest()
check(#STARTED == starts0 + 1 and snd2 ~= snd and snd2.kind == "3d" and snd2.pos.x == 10, "walking back starts it again at once, at the same spot")
check(chunk_no(snd2.parts[1]) > chunk_no(snd.parts[#snd.parts]), "and with current audio, not what was missed (chunk " .. chunk_no(snd2.parts[1]) .. " after " .. chunk_no(snd.parts[#snd.parts]) .. ")")
ACTOR_POS:set(300, 2, 40); click(tab, "btn_next")
local snd3 = next_sound(snd2, 6000)
check(snd2.stopped and snd3 and snd3.pos.x == 300 and snd3.pos.z == 40, "pressing next moves the radio to where the player stands")
run(3000)
check(snd3 and snd3:playing() and gapless(snd3) and #snd3.parts >= 5, "and it keeps playing there")
-- doppler: the engine plays a 3D sound faster or slower than real time while the player moves
DOPPLER = 1.08; run(6000); DOPPLER = 0.92; run(6000); DOPPLER = 1; run(6000)
check(CULLED == 0 and FREEZES == 0, "the engine never culled a playing sound, and playback off by 8% either way never led to a late attach (culled frames: " .. CULLED .. ", freezes: " .. FREEZES .. ", sounds: " .. #STARTED .. ")")
snd = newest()
WALL = false
stream.set_mode("head"); run(9000)
if os.getenv("SOAK") then local n, sil = #STARTED, SILENCES; run(tonumber(os.getenv("SOAK")) * 1000); print("SOAK head: new sounds " .. (#STARTED - n) .. ", freezes " .. FREEZES .. ", silences " .. (SILENCES - sil)) end
check(FREEZES == 0, "head mode: no late attach either")
check(stream.get_mode() == "head" and newest().kind == "2d" and snd.stopped, "head mode: old sound stopped, plays 2D")
stream.set_mode("off"); n = #SOUNDS; run(3000)
check(stream.get_mode() == "off" and #SOUNDS == n and SOUNDS[n].stopped, "off stops it")
check(api("/_state"):find('"device": "dev1"'), "playback was handed back to the desktop app")
check(count_files() == 0, "no chunk files left behind after stopping, " .. count_files() .. " on disk")
end

-- As shipped: a fresh copy of the script, as after loading a save or changing level. No click anywhere.
local function fresh_copy()
	local copy = load_script("spotify_radio_stream", true)
	for k, v in pairs(copy) do if type(v) == "function" then stream[k] = v end end   -- the PDA talks to this copy now
	return copy
end
KEEP2 = os.getenv("KEEP_DIR") .. ".chunks"; os.execute('rm -rf "' .. KEEP2 .. '"; mkdir -p "' .. KEEP2 .. '"')
local HEARD = io.open(os.getenv("KEEP_DIR") .. ".heard", "w")
-- Notes what the fake engine is playing and what the PDA claims; heard_check.py compares them with the audio.
local function sample(label)
	local o = newest()
	if not (o:playing() and o.heard and radio.shown.heard) then check(false, "nothing to sample: " .. label) return end
	run(200)
	local i = math.min(#o.parts, math.floor(o.heard / 1000) + 1)
	HEARD:write(table.concat({ label, o.parts[i]:match("[^\\]+$"), math.floor(o.heard - (i - 1) * 1000), radio.shown.uri, math.floor(radio.shown_position()) }, "\t"), "\n")
	HEARD:flush()
end
phone("play")
local shipped = fresh_copy()
n = #SOUNDS; t0 = socket.gettime()
shipped.on_game_start()
local auto, took = next_sound(SOUNDS[n], 8000)
check(auto and shipped.get_mode() == "head" and auto.kind == "2d", string.format("as shipped the music plays in game by itself after a load, %.1f s after the script starts", took))
check(took > 2.8 and took < 6.5, "it waits three seconds after the load, then takes under 3.5 s")
check(api("/_state"):find('"device": "pda"'), "and Spotify was moved to the game's device")
check(shipped.set_mode("world") == "head", "as shipped world mode cannot be selected")
run(5000)
check(shipped.lag_ms() >= 4000 and shipped.lag_ms() <= 9000, "about six seconds of audio are held: " .. shipped.lag_ms() .. " ms")
check(radio.shown.heard and tab.now_playing:GetText() ~= "Nothing playing", "the PDA shows the track and time of what is heard: " .. tab.now_playing:GetText() .. " | " .. tab.status:GetText())
sample("start")

-- volume buttons: the sound's own volume, at once; Spotify's volume is left alone
local vol0 = spotify("vol")
click(tab, "btn_vol_down")
check(math.abs(newest().volume - 0.9) < 0.001, "volume down changes the playing sound in the same frame: " .. newest().volume)
run(600)
check(tab.status:GetText():find("vol 90%%") and spotify("vol") == vol0, "the PDA shows it and Spotify's own volume is untouched: " .. tab.status:GetText())

-- pause and play in the PDA
local s1 = newest(); local at = heard_chunk(s1)
click(tab, "btn_pause")
check(s1.stopped, "pause in the PDA stops the sound in the same frame")
run(300)
check(tab.btn_play.shown and not tab.btn_pause.shown, "and the button flips without waiting for Spotify")
run(2500)
local p1 = radio.shown_position(); run(1000)
check(spotify("playing") == "false" and newest() == s1 and radio.shown_position() == p1, "Spotify is paused, nothing plays, the time stands still")
click(tab, "btn_play")
local s2; s2, took = next_sound(s1, 3000)
check(s2 and took < 0.5, string.format("play starts it again %.2f s later", took))
check(s2 and chunk_no(s2.parts[1]) - at >= 0 and chunk_no(s2.parts[1]) - at <= 1, "from where it stopped (chunk " .. (s2 and chunk_no(s2.parts[1]) or -1) .. ", was in " .. at .. ")")
run(3000)
check(spotify("playing") == "true" and s2:playing() and gapless(s2), "and it keeps playing")
sample("after pause")

-- next in the PDA
local title = tab.now_playing:GetText()
s1 = newest(); click(tab, "btn_next")
check(s1.stopped, "next in the PDA stops the old track in the same frame")
s2, took = next_sound(s1, 8000)
check(s2 and took < 3.5, string.format("the new track is heard %.1f s later", took))
run(2500)
check(tab.now_playing:GetText() ~= title and radio.shown.heard and radio.shown_position() < 7000, "and the PDA shows it from its start: " .. tab.now_playing:GetText() .. " | " .. tab.status:GetText())
sample("after next")

-- the same things done from another Spotify app
s1 = newest(); t0 = socket.gettime(); phone("pause")
check(wait_for(function() return s1.stopped end, 4000), string.format("paused from the phone: the sound stops %.1f s later", socket.gettime() - t0))
at = heard_chunk(s1); run(2000)
check(newest() == s1, "and stays stopped")
phone("play")
s2, took = next_sound(s1, 4000)
check(s2 and chunk_no(s2.parts[1]) - at >= 0 and chunk_no(s2.parts[1]) - at <= 1, string.format("resumed from the phone: plays again %.1f s later, from where it stopped", took))
run(3000); sample("after phone pause")
title = tab.now_playing:GetText()
s1 = newest(); t0 = socket.gettime(); phone("next")
check(wait_for(function() return s1.stopped end, 4000), string.format("skipped from the phone: the old track stops %.1f s later", socket.gettime() - t0))
s2, took = next_sound(s1, 8000)
run(2500)
check(s2 and tab.now_playing:GetText() ~= title and radio.shown_position() < 9000, string.format("and the new one is heard %.1f s after that: %s | %s", took, tab.now_playing:GetText(), tab.status:GetText()))
sample("after phone next")

-- a track ending by itself: Spotify moves on while the game still plays the end of the old one
local dur = spotify("idx") == "0" and 238000 or 200000
s1 = newest(); phone("seek", dur - 13000)
s2 = next_sound(s1, 8000)
run(2500)
check(s2 and radio.shown_position() >= dur - 13000 and radio.shown_position() <= dur - 6000, "a seek from the phone is followed: " .. tab.status:GetText())
sample("after seek")
title = tab.now_playing:GetText()
check(wait_for(function() return spotify("progress") + 0 < 10000 end, 15000), "Spotify reaches the next track")
local behind = dur - radio.shown_position()
check(tab.now_playing:GetText() == title and behind > 2000, string.format("while the PDA still shows the old one, %.1f s from its end", behind / 1000))
check(wait_for(function() return tab.now_playing:GetText() ~= title end, 12000), "the PDA changes over when the new track is heard: " .. tab.now_playing:GetText())
check(radio.shown_position() < 2500, "with its time starting near zero: " .. tab.status:GetText())
check(newest() == s2 and s2:playing() and gapless(s2), "and the sound was not interrupted")
run(2000); sample("after track end")

-- the game paused (pause key, console, alt-tab without g_always_active): no script updates, game time frozen
for _, secs in ipairs({ 7, 40 }) do
	s1 = newest(); at = heard_chunk(s1)
	pause_game(true); run(secs * 1000)
	check(spotify("playing") == "false", "game paused for " .. secs .. " s: the daemon paused Spotify")
	pause_game(false)
	for _, f in ipairs(cbs.actor_on_update) do f() end          -- first frame after the pause: scripts run before the sound update
	check(s1.stopped, "the old sound is stopped before the engine can resume it")
	s2, took = next_sound(s1, 5000)
	check(s2 and took < 1.5 and chunk_no(s2.parts[1]) - at >= 0 and chunk_no(s2.parts[1]) - at <= 1,
		string.format("the music continues %.1f s after the game does, from where it stopped (chunk %d, was in %d)", took, s2 and chunk_no(s2.parts[1]) or -1, at))
	run(4000)
	check(spotify("playing") == "true" and s2:playing() and gapless(s2), "Spotify was resumed and it keeps playing")
	sample("after a " .. secs .. " s game pause")
end
-- the game is away and someone starts the music from the phone anyway: nobody takes the chunks,
-- so the daemon stops making them; the game asks for the stream again when it is back
s1 = newest()
pause_game(true); run(5000); phone("play"); run(14000)
local piled = 0; for _ in io.popen('ls "' .. LEVEL_DIR .. '" 2>/dev/null'):lines() do piled = piled + 1 end
check(piled <= 22, "game away, music started from the phone: the daemon stops writing chunks (" .. piled .. " waiting)")
pause_game(false)
s2, took = next_sound(s1, 8000)
check(s2 and chunk_no(s2.parts[1]) <= 2 and gapless(s2), string.format("back in the game: a new stream plays %.1f s later", took))
run(4000)
s1 = newest(); at = heard_chunk(s1)
for _, f in ipairs(cbs.main_menu_on_init) do f({}) end
check(s1.stopped, "opening the main menu stops the sound")
s2 = next_sound(s1, 3000)
check(s2 and chunk_no(s2.parts[1]) - at <= 1, "and it picks up where it stopped")
run(2000)
EFF = 0; run(300)
check(newest() == s2 and s2.stopped, "effects volume at zero: sound stopped by the script, not culled by the engine")
EFF = 0.3
s1 = next_sound(s2, 3000)
check(s1, "volume back: playing again")

-- YouTube, and the PDA's switch between the two. fake_ytdlp.sh answers after 1.2 s like a real
-- lookup; ffmpeg decodes for real.
if os.getenv("SPOTIFY_RADIO_YTDLP") then
	local function yt() return radio.shown.uri:match("^yt:video:(.+)$") end
	run(3000)
	check(tab.btn_to_youtube.shown and not tab.btn_to_spotify.shown and tab.title_spotify.shown and not tab.title_youtube.shown,
		"SW: the PDA offers a switch to YouTube")
	local sp_uri, sp_pos = radio.shown.uri, radio.shown_position()
	local y1, y2 = newest(), nil
	click(tab, "btn_to_youtube")
	local stopped = y1.stopped
	run(300)
	check(stopped and tab.title_youtube.shown and not tab.title_spotify.shown and tab.btn_to_spotify.shown and not tab.btn_to_youtube.shown,
		"SW: switching to YouTube stops the music in the same frame and the PDA says YouTube at once")
	run(2200)
	check(spotify("playing") == "false" and newest() == y1 and tab.now_playing:GetText() == "Nothing playing" and not tab.btn_playlists.shown,
		"SW: Spotify is paused, YouTube has nothing yet, and there is no My playlists: " .. tab.now_playing:GetText() .. " | " .. tab.status:GetText())
	click(tab, "btn_search")
	check(dlg:IsShown() and dlg.caption:GetText() == "Search YouTube Music", "SW: the search box says YouTube Music: " .. dlg.caption:GetText())
	dlg.input:SetText("creep"); dlg._cb["fld_input" .. ui_events.EDIT_TEXT_COMMIT](); run(1500)
	check(tab.rows[1].text:GetText() == "[track] Creep - Radiohead (3:59)" and tab.rows[4].text:GetText():find("^%[album%] OK Computer")
		and tab.rows[5].text:GetText():find("^%[playlist%] Fake Mix") and not tab.rows[4].queue.shown,
		"SW: and the search goes there, no prefix needed: songs, albums and playlists: " .. tab.rows[1].text:GetText() .. " | " .. tab.rows[4].text:GetText() .. " | " .. tab.rows[5].text:GetText())
	click(tab, "row_play_1")
	check(y1.stopped, "YT: playing a result stops what was playing in the same frame")
	y2, took = next_sound(y1, 12000)
	check(y2 and took < 4, string.format("YT: the track is heard %.1f s after the click (the lookup takes 1.2 s here)", took))
	run(2500)
	check(yt() == "vid00000001" and tab.now_playing:GetText() == "Radiohead - Creep" and radio.shown_position() < 7000 and tab.status:GetText():find("YouTube"),
		"YT: the PDA shows it from its start: " .. tab.now_playing:GetText() .. " | " .. tab.status:GetText())
	check(spotify("playing") == "false", "YT: Spotify was paused")
	click(tab, "row_queue_2"); run(1500)                 -- Karma Police comes next

	-- pause and play
	y1 = newest(); at = heard_chunk(y1)
	click(tab, "btn_pause")
	check(y1.stopped, "YT: pause stops the sound in the same frame")
	run(3000)
	local yp = radio.shown_position(); run(800)
	check(newest() == y1 and radio.shown_position() == yp and tab.btn_play.shown, "YT: nothing plays while paused and the time stands still")
	click(tab, "btn_play")
	y2, took = next_sound(y1, 3000)
	check(y2 and took < 0.5 and chunk_no(y2.parts[1]) - at >= 0 and chunk_no(y2.parts[1]) - at <= 1, string.format("YT: play continues %.2f s later from where it stopped", took))
	run(2000)

	-- the game paused: the daemon pauses YouTube and carries on when the game is back
	y1 = newest(); at = heard_chunk(y1)
	pause_game(true); run(7000); pause_game(false)
	for _, f in ipairs(cbs.actor_on_update) do f() end
	y2, took = next_sound(y1, 5000)
	check(y2 and took < 1.5 and chunk_no(y2.parts[1]) - at >= 0 and chunk_no(y2.parts[1]) - at <= 1,
		string.format("YT: after a 7 s game pause it continues %.1f s later from where it stopped", took))
	run(1500)
	check(yt() == "vid00000001" and radio.shown_position() > 5000 and radio.shown_position() < 16000, "YT: and the PDA time went on from there: " .. tab.status:GetText())

	-- next plays the queued track, looked up while the first one played; previous goes back
	y1 = newest(); click(tab, "btn_next")
	check(y1.stopped, "YT: next stops the old track in the same frame")
	y2, took = next_sound(y1, 8000); run(2500)
	check(y2 and took < 1.5 and yt() == "vid00000002", string.format("YT: next plays the queued track %.1f s later: %s", took, tab.now_playing:GetText()))
	y1 = newest(); click(tab, "btn_prev")
	y2, took = next_sound(y1, 8000); run(2500)
	check(y2 and yt() == "vid00000001", "YT: previous goes back to the track before: " .. tab.now_playing:GetText())

	-- switching back and forth: each source carries on from what the game was playing when it was left
	local yt_pos = radio.shown_position()
	y1 = newest(); click(tab, "btn_to_spotify")
	stopped = y1.stopped
	y2, took = next_sound(y1, 8000); run(3000)
	check(stopped and tab.title_spotify.shown and tab.btn_to_youtube.shown and tab.btn_playlists.shown, "SW: switching to Spotify stops YouTube in the same frame, and the PDA says Spotify")
	local off = radio.shown_position() - 3000 - sp_pos
	check(y2 and took < 3 and radio.shown.uri == sp_uri and spotify("playing") == "true" and math.abs(off) < 1500,
		string.format("SW: Spotify plays on %.1f s later, from where it was left (%+.1f s): %s | %s", took, off / 1000, tab.now_playing:GetText(), tab.status:GetText()))
	sample("back on Spotify")
	y1 = newest(); click(tab, "btn_to_youtube")
	y2, took = next_sound(y1, 8000); run(3000)
	off = radio.shown_position() - 3000 - yt_pos
	check(y2 and took < 1 and yt() == "vid00000001" and spotify("playing") == "false" and math.abs(off) < 1500,
		string.format("SW: back on YouTube the track goes on %.1f s later, from where it was left (%+.1f s): %s", took, off / 1000, tab.status:GetText()))
	sample("back on YouTube")

	-- the list runs out: a radio of the last track plays on, without a break
	y1 = newest(); click(tab, "btn_next"); y2 = next_sound(y1, 8000); run(1000)
	check(yt() == "vid00000002", "YT: next once more: " .. tab.now_playing:GetText())
	y1 = newest()
	check(wait_for(function() return yt() == "vid00000003" end, 25000), "YT: when the list is over, a radio of its last track plays on: " .. tab.now_playing:GetText())
	check(radio.shown_position() < 2500, "YT: from the start of its first track: " .. tab.status:GetText())
	check(newest() == y1 and y1:playing() and gapless(y1), "YT: and the sound was not interrupted")

	-- an album from the search
	y1 = newest(); click(tab, "row_play_4")
	y2, took = next_sound(y1, 10000); run(2000)
	check(y2 and yt() == "vid00000006", string.format("YT: an album plays from its first track, heard %.1f s after the click: %s", took, tab.now_playing:GetText()))

	-- the stream is stopped and asked for again: YouTube waits, paused, until play
	shipped.set_mode("off"); run(1500)
	shipped.set_mode("head"); run(3000)
	check(tab.btn_play.shown and yt() == "vid00000006", "YT: after the stream was stopped and asked for again it waits, paused: " .. tab.now_playing:GetText() .. " | " .. tab.status:GetText())
	y1 = newest(); click(tab, "btn_play")
	y2, took = next_sound(y1, 6000); run(2000)
	check(y2 and y2:playing() and yt() == "vid00000006", string.format("YT: and play carries on %.1f s later", took))

	-- the first stream address is refused: a new one is asked for at once
	y1 = newest(); stream.user("change"); radio.play_uri("yt:video:oncebad0001")
	y2, took = next_sound(y1, 12000); run(2000)
	check(y2 and took < 5 and yt() == "oncebad0001", string.format("YT: a refused stream address costs one more lookup, heard %.1f s after the click", took))

	-- a track that cannot be played
	click(tab, "row_play_3")
	check(wait_for(function() return tab.status:GetText():find("Video unavailable", 1, true) ~= nil end, 8000), "YT: a track that cannot be played says why in the PDA: " .. tab.status:GetText())
	run(1500)
	check(tab.now_playing:GetText() == "Nothing playing", "YT: and nothing plays: " .. tab.now_playing:GetText())

	-- a Spotify result (from a search made before switching) brings Spotify back, switch and all
	wait_for(function() return not tab.status:GetText():find("^!") end, 8000)   -- the error has had its time
	y1 = newest(); stream.user("change"); radio.play_uri("spotify:track:2")
	y2, took = next_sound(y1, 10000); run(3000)
	check(y2 and radio.shown.uri == "spotify:track:2" and spotify("playing") == "true",
		string.format("YT: a Spotify result brings Spotify back, heard %.1f s after the click: %s", took, tab.now_playing:GetText()))
	check(not tab.status:GetText():find("YouTube") and tab.title_spotify.shown and tab.btn_to_youtube.shown,
		"YT: the PDA no longer says YouTube and the switch is back on Spotify: " .. tab.status:GetText())
	radio.search("yt creep"); run(1500)
	check(tab.rows[1].text:GetText() == "[track] Creep - Radiohead (3:59)", "YT: \"yt ...\" in the search box still searches YouTube Music: " .. tab.rows[1].text:GetText())
	run(2000)
end
run(12000)
check(shipped.lag_ms() >= 4000 and shipped.lag_ms() <= 9000, "after all that about six seconds are held again: " .. shipped.lag_ms() .. " ms")
check(CULLED == 0 and FREEZES == 0, "never a culled sound or a late attach (culled frames: " .. CULLED .. ", freezes: " .. FREEZES .. ")")
shipped.set_mode("off"); run(3000)
check(count_files() == 0, "no chunk files left behind, " .. count_files() .. " on disk")
KEEP2 = nil; HEARD:close()

-- the volume set in the PDA survives a level change (a new copy of the script)
local again = fresh_copy()
s1 = newest(); again.on_game_start()
s2 = next_sound(s1, 8000)
check(s2 and again.get_volume() == 90 and math.abs(s2.volume - 0.9) < 0.001, "the volume set in the PDA is kept across a level change: " .. tostring(again.get_volume()))
again.set_mode("off"); run(3000)
check(count_files() == 0 and FREEZES == 0, "clean at the end, " .. count_files() .. " files on disk")
print("WORLD_PARTS " .. world_parts)

-- paging with fake results
radio.results = {}; for i = 1, 30 do radio.results[i] = { kind = "track", uri = "u" .. i, title = string.rep("T", 100) .. i, subtitle = "" } end
radio.results_title = "many"; radio.results_version = radio.results_version + 1; run(400)
check(tab.btn_page_next.shown and tab.list_title:GetText() == "many  (30)  page 1/3", "paging title: " .. tab.list_title:GetText())
check(#tab.rows[1].text:GetText() == 70, "long titles are cut")
click(tab, "btn_page_next"); click(tab, "btn_page_next"); click(tab, "btn_page_next")
check(tab.page == 3 and tab.rows[8].st.shown and not tab.rows[9].st.shown, "last page shows 8 rows")
click(tab, "btn_page_prev"); check(tab.page == 2, "page back")
radio.send("bogus"); run(500)
check(tab.status:GetText() == "! Unknown command: bogus", "error shown in tab: " .. tab.status:GetText())
if radio.link_mode == "file" then
	os.execute("pkill -f SpotifyRadio.dll"); run(13500)
	check(not radio.is_connected() and tab.now_playing:GetText() == "Not connected", "daemon death is noticed: " .. tab.status:GetText())
	click(tab, "btn_next"); run(300)
	check(tab.status:GetText() == "! Daemon is not running - start run.bat", "click while down explains itself")
	os.execute("dotnet ../daemon/bin/Release/net7.0/SpotifyRadio.dll > daemon3.log 2>&1 &"); run(8000)
	check(radio.is_connected() and tab.now_playing:GetText() ~= "Not connected", "daemon restart is picked up without replaying old clicks: " .. tab.now_playing:GetText())
	check(not io.open("daemon3.log"):read("*a"):find("<- next"), "stale commands are not replayed")
end
local dlog = io.open("daemon_" .. (arg[2] or "file") .. ".log"):read("*a")
check(dlog:find("game: PDA hook installed", 1, true) and dlog:find("game: radio tab opened", 1, true), "the daemon window says the PDA hook is in place")
os.exit(fails)
