#!/usr/bin/env python3
"""A small stand-in for GitHub and a package page on this machine, for tests/compare-ember.sh.

    ember-fake-github.py <fixtures folder> <port file>

Serves the files of the folder under /files/ (an index, packages), and the part of the REST API of GitHub ember uses for the repository o/gpkg:
releases/latest and releases/tags/v1.0.0 (with the asset gpkg-1.0.0.fpk and its digest, downloaded through a redirect), commits/<ref>, and
tarball/<commit> (the source folder `src/` of the fixtures as a .tar.gz with a pax header, as GitHub makes it). Writes the port it listens on
into the port file. Only for tests: plain http on 127.0.0.1.
"""
import hashlib
import http.server
import io
import json
import os
import sys
import tarfile

ROOT = os.path.abspath(sys.argv[1])
PORT_FILE = sys.argv[2]
TAG_COMMIT = "a" * 40
MAIN_COMMIT = "b" * 40


def tarball():
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz", format=tarfile.PAX_FORMAT) as tar:
        tar.pax_headers = {"comment": MAIN_COMMIT}
        top = "o-gpkg-" + MAIN_COMMIT[:7]
        info = tarfile.TarInfo(top)
        info.type = tarfile.DIRTYPE
        tar.addfile(info)
        src = os.path.join(ROOT, "src")
        for dirpath, dirnames, filenames in os.walk(src):
            dirnames.sort()
            for name in sorted(filenames):
                path = os.path.join(dirpath, name)
                rel = os.path.relpath(path, src).replace(os.sep, "/")
                data = open(path, "rb").read()
                info = tarfile.TarInfo(top + "/" + rel)
                info.size = len(data)
                info.mtime = 0
                tar.addfile(info, io.BytesIO(data))
    return buf.getvalue()


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def send(self, status, body, ctype="application/json", headers=None):
        if isinstance(body, str):
            body = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def base(self):
        return "http://127.0.0.1:%d" % self.server.server_port

    def release(self):
        fpk = os.path.join(ROOT, "files", "gpkg-1.0.0.fpk")
        data = open(fpk, "rb").read()
        return {"tag_name": "v1.0.0", "assets": [{
            "name": "gpkg-1.0.0.fpk", "size": len(data), "url": self.base() + "/repos/o/gpkg/releases/assets/1",
            "browser_download_url": self.base() + "/download/gpkg-1.0.0.fpk", "digest": "sha256:" + hashlib.sha256(data).hexdigest()}]}

    def do_GET(self):
        p = self.path.split("?")[0]
        if p.startswith("/files/"):
            path = os.path.join(ROOT, "files", p[len("/files/"):])
            if os.path.isfile(path):
                return self.send(200, open(path, "rb").read(), "application/octet-stream")
            return self.send(404, "{}")
        if p.startswith("/download/"):
            return self.send(302, "", headers={"Location": "/files/" + p[len("/download/"):]})
        if p in ("/repos/o/gpkg/releases/latest", "/repos/o/gpkg/releases/tags/v1.0.0"):
            return self.send(200, json.dumps(self.release()))
        if p == "/repos/o/gpkg/commits/v1.0.0":
            return self.send(200, json.dumps({"sha": TAG_COMMIT}))
        if p in ("/repos/o/gpkg/commits/main", "/repos/o/gpkg/commits/" + MAIN_COMMIT):
            return self.send(200, json.dumps({"sha": MAIN_COMMIT}))
        if p == "/repos/o/gpkg/tarball/" + MAIN_COMMIT:
            return self.send(302, "", headers={"Location": "/codeload/" + MAIN_COMMIT})
        if p == "/codeload/" + MAIN_COMMIT:
            return self.send(200, tarball(), "application/x-gzip")
        if p.startswith("/repos/o/limited/"):
            return self.send(403, json.dumps({"message": "API rate limit exceeded"}), headers={"x-ratelimit-remaining": "0", "retry-after": "60"})
        return self.send(404, json.dumps({"message": "Not Found"}))


server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
with open(PORT_FILE, "w") as f:
    f.write(str(server.server_port))
server.serve_forever()
