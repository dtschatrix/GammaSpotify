import json, sys, urllib.parse
from http.server import BaseHTTPRequestHandler, HTTPServer

TRACKS = [("spotify:track:1", "Archangel", "Burial", 238000), ("spotify:track:2", "Señorita Ünïcode – “Тест”", "Молчат Дома", 200000)]
S = {"device": "dev1", "playing": False, "idx": 0, "vol": 50, "active": False, "shuffle": False, "token": "tok1", "log": [], "context": None}

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
        if p == "/_state": return self._send(200, {"device": S["device"], "playing": bool(S.get("playing")), "transfers": S.get("transfers", [])})
        if p == "/_query": return self._send(200, {"q": S.get("last_query")})
        if not self._auth(): return
        if p == "/v1/me/player" and method == "GET":
            if not S["active"]: return self._send(204)
            t = TRACKS[S["idx"]]
            return self._send(200, {"is_playing": S["playing"], "progress_ms": 12345, "shuffle_state": S["shuffle"],
                "device": {"id": S["device"], "name": {"dev1": "DESKTOP", "pda": "STALKER PDA"}.get(S["device"], "?"), "volume_percent": S["vol"]},
                "item": {"uri": t[0], "name": t[1], "duration_ms": t[3], "artists": [{"name": t[2]}]}})
        if p == "/v1/me/player/devices":
            return self._send(200, {"devices": [{"id": "phone", "name": "Phone", "type": "Smartphone", "is_restricted": False},
                                                 {"id": "dev1", "name": "DESKTOP", "type": "Computer", "is_restricted": False, "is_active": S["active"] and S["device"] == "dev1"},
                                                 {"id": "pda", "name": "STALKER PDA", "type": "Speaker", "is_restricted": False, "is_active": S["active"] and S["device"] == "pda"}]})
        if p == "/v1/me/player" and method == "PUT":
            b = json.loads(body); S["device"] = b["device_ids"][0]; S["active"] = True
            if b.get("play"): S["playing"] = True
            S["transfers"] = S.get("transfers", []) + [S["device"]]
            return self._send(204)

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
                elif S["playing"]:
                    return self._send(403, {"error": {"status": 403, "message": "Player command failed: Restriction violated", "reason": "UNKNOWN"}})
                S["playing"] = True
            elif act == "pause": S["playing"] = False
            elif act == "next": S["idx"] = (S["idx"] + 1) % len(TRACKS)
            elif act == "previous": S["idx"] = (S["idx"] - 1) % len(TRACKS)
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

HTTPServer(("127.0.0.1", 47900), H).serve_forever()
