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
		if self.kind == "2d" then return EFF < 0.01 end
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
check(cbs.on_key_press == nil or true, "loaded")
run(700)
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
local function api(path) return io.popen("curl -s http://127.0.0.1:47900" .. path):read("*a") end
HOP = 1000
KEEP = os.getenv("KEEP_DIR"); os.execute('rm -rf "' .. KEEP .. '"; mkdir -p "' .. KEEP .. '"')
check(tab.btn_stream == nil and stream.get_mode() == "off", "no stream switch in the PDA; nothing streams while disabled")
stream.set_mode("world"); run(400)
check(stream.get_mode() == "world", "world mode (enabled for the tests only)")
local t_start = time_global()
run(15600)
local snd = SOUNDS[#SOUNDS]
check(api("/_state"):find('"device": "pda"'), "playback was moved to the game's Spotify device")
check(#STARTED == 1 and snd.kind == "3d", "one continuous 3D sound, started once (starts: " .. #STARTED .. ")")
check(#snd.parts >= 10 and #snd.parts <= 16 and snd:playing(), "about one chunk per second is chained on (real-time pacing): " .. #snd.parts .. " in 16 s")
check(FREEZES == 0 and SILENCES == 0 and REFUSED > 0, "the engine never read to the end of its data (silences " .. SILENCES .. ", freezes " .. FREEZES .. ", refused offers " .. REFUSED .. ")")
check(count_files() <= 9, "finished chunk files are deleted as it goes, " .. count_files() .. " on disk")
local starts0 = #STARTED
local world_parts = #snd.parts
KEEP = nil

-- world mode: distance, walls, pauses. The engine must never get to cull the playing sound.
local function chunk_no(p) return tonumber(p:match("(%d+)$")) end
local function gapless(o) for i = 2, #o.parts do if chunk_no(o.parts[i]) ~= chunk_no(o.parts[i - 1]) + 1 then return false end end return true end
check(snd.stype == snd_type.world_ambient and snd.owner == nil and snd.max_distance >= 500, "world sound is un-occluded, ownerless and has a huge engine range")
check(math.abs(snd.volume - 1) < 0.05, "full volume next to the radio: " .. snd.volume)
WALL = true; ACTOR_POS:set(10 + 23.5, 0, -5); run(1500)
check(math.abs(snd.volume - 0.5) < 0.02, "half volume half way out: " .. snd.volume)
ACTOR_POS:set(10 + 44, 0, -5); run(2500)
check(#STARTED == starts0 and snd:playing() and snd.volume * EFF >= 0.02 and snd.volume < 0.12, "at the edge of earshot the same sound keeps running, quiet but not cullable: " .. snd.volume)
check(gapless(snd) and #snd.parts >= world_parts + 3, "and is still chained without a gap (" .. #snd.parts .. " chunks)")
ACTOR_POS:set(10 + 100, 0, -5); run(4000)     -- far out of range
check(snd.stopped and #STARTED == starts0, "out of earshot the sound is stopped and none is started")
check(count_files() <= 6, "out of earshot only the newest chunks are kept, " .. count_files() .. " on disk")
ACTOR_POS:set(10 + 5, 0, -5); run(700)
local snd2 = SOUNDS[#SOUNDS]
check(#STARTED == starts0 + 1 and snd2 ~= snd and snd2.kind == "3d" and snd2.pos.x == 10, "walking back starts it again at once, at the same spot")
check(chunk_no(snd2.parts[1]) > chunk_no(snd.parts[#snd.parts]), "and with current audio, not what was missed (chunk " .. chunk_no(snd2.parts[1]) .. " after " .. chunk_no(snd.parts[#snd.parts]) .. ")")
ACTOR_POS:set(300, 2, 40); click(tab, "btn_next"); run(300)
check(#STARTED == starts0 + 1 and snd2.pos.x == 300 and snd2.pos.z == 40 and not snd2.stopped, "pressing next moves the radio to where the player stands")
run(3000)
check(snd2:playing() and gapless(snd2) and #snd2.parts >= 5, "and it keeps playing there")
-- game paused (alt-tab, pause key): no script updates, game time frozen, chunks keep coming
pause_game(true); run(5000); pause_game(false)
for _, f in ipairs(cbs.actor_on_update) do f() end          -- first frame after the pause: scripts run before the sound update
check(snd2.stopped, "after a pause the old sound is stopped before the engine can resume it")
run(1500)
local snd3 = SOUNDS[#SOUNDS]
check(snd3 ~= snd2 and snd3:playing() and chunk_no(snd3.parts[1]) >= chunk_no(snd2.parts[#snd2.parts]) + 2, "and a new one starts from current audio (chunk " .. chunk_no(snd3.parts[1]) .. " after " .. chunk_no(snd2.parts[#snd2.parts]) .. ")")
-- a long stay in the menu: the daemon gives up on the game meanwhile, so the stream is asked for again
pause_game(true); run(17000); pause_game(false); run(9000)
local snd_long = SOUNDS[#SOUNDS]
check(snd3.stopped and snd_long ~= snd3 and snd_long:playing() and chunk_no(snd_long.parts[1]) <= 3, "after a long pause the stream is requested again and plays (first chunk " .. chunk_no(snd_long.parts[1]) .. ")")
snd3 = snd_long
for _, f in ipairs(cbs.main_menu_on_init) do f({}) end
check(snd3.stopped, "opening the main menu stops the sound")
run(5500)
local snd4 = SOUNDS[#SOUNDS]
EFF = 0; run(300)
check(snd4 ~= snd3 and snd4.stopped, "effects volume at zero: sound stopped by the script, not culled by the engine")
EFF = 0.3; run(5500)
snd = SOUNDS[#SOUNDS]
check(snd ~= snd4 and snd:playing(), "volume back: playing again")
-- doppler: the engine plays a 3D sound faster or slower than real time while the player moves
DOPPLER = 1.08; run(6000); DOPPLER = 0.92; run(6000); DOPPLER = 1; run(6000)
check(FREEZES == 0, "playback speed off by 8% either way never leads to a late attach (sounds started: " .. #STARTED .. ")")
snd = SOUNDS[#SOUNDS]
check(CULLED == 0 and FREEZES == 0, "the engine never culled a playing sound and never got a late attach (culled frames: " .. CULLED .. ", freezes: " .. FREEZES .. ")")
WALL = false
stream.set_mode("head"); run(9000)
if os.getenv("SOAK") then local n, sil = #STARTED, SILENCES; run(tonumber(os.getenv("SOAK")) * 1000); print("SOAK head: new sounds " .. (#STARTED - n) .. ", freezes " .. FREEZES .. ", silences " .. (SILENCES - sil)) end
check(FREEZES == 0, "head mode: no late attach either")
check(stream.get_mode() == "head" and SOUNDS[#SOUNDS].kind == "2d" and snd.stopped, "head mode: old sound stopped, plays 2D")
stream.set_mode("off"); local n = #SOUNDS; run(3000)
check(stream.get_mode() == "off" and #SOUNDS == n and SOUNDS[n].stopped, "off stops it")
check(api("/_state"):find('"device": "dev1"'), "playback was handed back to the desktop app")
check(count_files() == 0, "no chunk files left behind after stopping, " .. count_files() .. " on disk")

-- As shipped: a fresh copy of the script, as after loading a save or changing level. No click anywhere.
local shipped = load_script("spotify_radio_stream", true)
shipped.on_game_start()
n = #SOUNDS; run(9000)
local auto = SOUNDS[#SOUNDS]
check(shipped.get_mode() == "head" and #SOUNDS > n and auto.kind == "2d" and auto:playing(), "as shipped the music plays in game by itself after a load")
check(api("/_state"):find('"device": "pda"'), "and Spotify was moved to the game's device")
check(shipped.set_mode("world") == "head", "as shipped world mode cannot be selected")
-- The game goes away for long (loading, alt-tabbed in a menu, quit): the daemon pauses Spotify, and resumes it when the game is back.
radio.send("play"); run(1500)
pause_game(true); run(45000)
if arg[2] ~= "socket" then   -- over TCP a paused game still counts as present; only a closed one is "away"
	check(api("/_state"):find('"playing": false'), "game away for long: the daemon paused Spotify")
end
pause_game(false); run(12000)
local back = SOUNDS[#SOUNDS]
check(api("/_state"):find('"playing": true') and back ~= auto and back:playing(), "game back: Spotify resumed and the music plays again")
check(FREEZES == 0, "still no late attach")
shipped.set_mode("off"); run(3000)
check(count_files() == 0, "no chunk files left behind, " .. count_files() .. " on disk")
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
os.exit(fails)
