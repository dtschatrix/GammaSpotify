# Stands in for Spotify's Web API. Like Spotify it only knows where playback was last reported
# to be and counts on from there; fake_librespot.py does the reporting when the game's device plays.
import json, sys, time, urllib.parse
from http.server import BaseHTTPRequestHandler, HTTPServer

TRACKS = [("spotify:track:1", "Archangel", "Burial", 238000), ("spotify:track:2", "Señorita Ünïcode – “Тест”", "Молчат Дома", 200000)]
S = {"device": "dev1", "playing": False, "idx": 0, "vol": 50, "active": False, "shuffle": False, "token": "tok1", "log": [], "context": None,
     "pos": 12345, "at": time.monotonic(), "gen": 0}

def progress():
    p = S["pos"] + ((time.monotonic() - S["at"]) * 1000 if S["playing"] else 0)
    return int(min(p, TRACKS[S["idx"]][3]))
def report(pos, jump=False):
    S["pos"], S["at"] = pos, time.monotonic()
    if jump: S["gen"] += 1          # playback restarts somewhere else: the device has to load
def do_act(name, arg=None):
    if name == "play":
        if not S["playing"]: S["at"] = time.monotonic(); S["playing"] = True
    elif name == "pause":
        S["pos"] = progress(); S["playing"] = False
    elif name in ("next", "previous"):
        S["idx"] = (S["idx"] + (1 if name == "next" else -1)) % len(TRACKS); S["playing"] = True; report(0, True)
    elif name == "seek":
        report(int(arg), True)

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def _send(self, code, obj=None):
        body = b"" if obj is None else json.dumps(obj).encode()
        self.send_response(code); self.send_header("Content-Length", str(len(body)))
        if body: self.send_header("Content-Type", "application/json")
        self.end_headers(); self.wfile.write(body)
    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n).decode() if n else ""
    def _auth(self):
        if self.headers.get("Authorization") != "Bearer " + S["token"]:
            self._send(401, {"error": {"status": 401, "message": "The access token expired"}}); return False
        return True
    def handle_any(self, method):
        u = urllib.parse.urlparse(self.path); q = urllib.parse.parse_qs(u.query); p = u.path; body = self._body()
        S["log"].append(f"{method} {self.path} {body}")
        if p == "/api/token":
            form = urllib.parse.parse_qs(body)
            if form.get("grant_type") == ["refresh_token"] and form.get("refresh_token") == ["refresh1"]:
                S["token"] = "tok2"; return self._send(200, {"access_token": "tok2", "expires_in": 3600, "refresh_token": "refresh2"})
            if form.get("grant_type") == ["authorization_code"] and form.get("code") == ["code1"] and len(form.get("code_verifier", [""])[0]) >= 43 and form.get("redirect_uri") == ["http://127.0.0.1:47801/callback"]:
                S["token"] = "tok3"; return self._send(200, {"access_token": "tok3", "expires_in": 3600, "refresh_token": "refresh3"})
            return self._send(400, {"error": "invalid_grant"})
        if p == "/_log": return self._send(200, S["log"])
        if p == "/_state": return self._send(200, {"device": S["device"], "playing": bool(S.get("playing")), "transfers": S.get("transfers", []),
                                                   "idx": S["idx"], "progress": progress(), "vol": S["vol"]})
        if p == "/_ctl":      # for fake_librespot
            return self._send(200, {"device": S["device"], "playing": S["playing"], "idx": S["idx"], "gen": S["gen"], "pos": S["pos"],
                                    "progress": progress(), "durations": [t[3] for t in TRACKS]})
        if p == "/_report": report(int(q["pos"][0])); return self._send(200, {})
        if p == "/_advance":  # the device played a track to its end and went on to the next
            S["idx"] = (S["idx"] + 1) % len(TRACKS); report(0); return self._send(200, {})
        if p == "/_do":       # something done in another Spotify app
            do_act(q["act"][0], q.get("ms", [None])[0]); return self._send(200, {})
        if p == "/_query": return self._send(200, {"q": S.get("last_query")})
        if p == "/youtubei/v1/search":   # YouTube Music's search, answered per filter (songs, albums, playlists)
            req = json.loads(body or "{}"); S["yt_query"] = req.get("query")
            col = lambda t: {"musicResponsiveListItemFlexColumnRenderer": {"text": {"runs": [{"text": t}]}}}
            def song(vid, title, sub): return {"musicResponsiveListItemRenderer": {"flexColumns": [col(title), col(sub)], "playlistItemData": {"videoId": vid}}}
            def browse(bid, title, sub): return {"musicResponsiveListItemRenderer": {"flexColumns": [col(title), col(sub)],
                                                 "navigationEndpoint": {"browseEndpoint": {"browseId": bid}}}}
            f = req.get("params", "")
            rows = ([song("vid00000001", "Creep", "Radiohead \u2022 Pablo Honey \u2022 3:59"),
                     song("vid00000002", "Karma Police", "Radiohead \u2022 OK Computer \u2022 4:21"),
                     song("failfailfai", "Broken", "Nobody \u2022 1:00")] if f.startswith("EgWKAQII") else
                    [browse("MPREb_fake1", "OK Computer", "Album \u2022 Radiohead \u2022 1997")] if f.startswith("EgWKAQIY") else
                    [browse("VLPLfake1", "Fake Mix", "someone \u2022 12 views")])
            return self._send(200, {"contents": {"tabbedSearchResultsRenderer": {"tabs": [{"tabRenderer": {"content": {"sectionListRenderer": {
                "contents": [{"musicShelfRenderer": {"contents": rows}}]}}}}]}}})
        if not self._auth(): return
        if p == "/v1/me/player" and method == "GET":
            if not S["active"]: return self._send(204)
            t = TRACKS[S["idx"]]
            return self._send(200, {"is_playing": S["playing"], "progress_ms": progress(), "shuffle_state": S["shuffle"],
                "device": {"id": S["device"], "name": {"dev1": "DESKTOP", "pda": "STALKER PDA"}.get(S["device"], "?"), "volume_percent": S["vol"]},
                "item": {"uri": t[0], "name": t[1], "duration_ms": t[3], "artists": [{"name": t[2]}]}})
        if p == "/v1/me/player/devices":
            return self._send(200, {"devices": [{"id": "phone", "name": "Phone", "type": "Smartphone", "is_restricted": False},
                                                 {"id": "dev1", "name": "DESKTOP", "type": "Computer", "is_restricted": False, "is_active": S["active"] and S["device"] == "dev1"},
                                                 {"id": "pda", "name": "STALKER PDA", "type": "Speaker", "is_restricted": False, "is_active": S["active"] and S["device"] == "pda"}]})
        if p == "/v1/me/player" and method == "PUT":
            b = json.loads(body); pos = progress(); S["device"] = b["device_ids"][0]; S["active"] = True
            if b.get("play"): S["playing"] = True
            report(pos, True)
            S["transfers"] = S.get("transfers", []) + [S["device"]]
            return self._send(204)

        if p == "/v1/me/player/queue" and method == "GET":   # what plays next: the other test track
            nxt = TRACKS[(S["idx"] + 1) % len(TRACKS)]
            return self._send(200, {"currently_playing": None, "queue": [{"uri": nxt[0], "name": nxt[1], "artists": [{"name": nxt[2]}]}]})
        if p.startswith("/v1/me/player/"):
            if not S["active"] and "device_id" not in q:
                return self._send(404, {"error": {"status": 404, "message": "Player command failed: No active device found", "reason": "NO_ACTIVE_DEVICE"}})
            if "device_id" in q: S["active"] = True
            act = p.rsplit("/", 1)[1]
            if act == "play":
                if body:
                    b = json.loads(body)
                    if "uris" in b: S["idx"] = [t[0] for t in TRACKS].index(b["uris"][0])
                    if "context_uri" in b: S["context"] = b["context_uri"]
                    S["playing"] = True; report(0, True)
                elif S["playing"]:
                    return self._send(403, {"error": {"status": 403, "message": "Player command failed: Restriction violated", "reason": "UNKNOWN"}})
                else: do_act("play")
            elif act in ("pause", "next", "previous"): do_act(act)
            elif act == "seek": do_act("seek", q["position_ms"][0])
            elif act == "volume": S["vol"] = int(q["volume_percent"][0])
            elif act == "shuffle": S["shuffle"] = q["state"][0] == "true"
            elif act == "queue": pass
            return self._send(204 if act != "play" else 200, None)
        if p == "/v1/search":
            S["last_query"] = q["q"][0]
            assert int(q["limit"][0]) <= 10
            return self._send(200, {"tracks": {"items": [{"uri": t[0], "name": t[1], "artists": [{"name": t[2]}]} for t in TRACKS]},
                "playlists": {"items": [None, {"uri": "spotify:playlist:p1", "name": "Dark\tAmbient", "owner": {"display_name": "someone"}}]},
                "albums": {"items": [{"uri": "spotify:album:a1", "name": "Untrue", "artists": [{"name": "Burial"}]}]}})
        if p == "/v1/me/playlists":
            return self._send(200, {"items": [{"uri": "spotify:playlist:mine", "name": "Zone walk", "owner": {"display_name": "me"}}]})
        self._send(404, {"error": {"status": 404, "message": "not found"}})
    def do_GET(self): self.handle_any("GET")
    def do_PUT(self): self.handle_any("PUT")
    def do_POST(self): self.handle_any("POST")

from http.server import ThreadingHTTPServer
ThreadingHTTPServer.request_queue_size = 128   # the daemon sends several requests at once (YouTube Music search)
ThreadingHTTPServer.daemon_threads = True
ThreadingHTTPServer(("127.0.0.1", 47900), H).serve_forever()
