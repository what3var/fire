# tests/model-corpus.py <out folder> <repository>: a corpus of model files for comparing the model converter of forge (C#) with that of forge-c
# (`tests/compare-frontend.sh ... model`, docs/spec/FORGE_C.md Phase D 4): the models of src/fire.Project, the robot of the sample as .glb and with an
# external buffer, random glTF (every component type, normalized, strides, offsets, matrices, trees, scenes, materials, animations LINEAR/STEP/CUBICSPLINE),
# random .fblocks and .fmesh, the error paths of each (with the exceptions of .NET they end in) and damaged copies (cut off, a character removed, a piece
# repeated; bytes of a .glb changed) - always the same ones. Writes <out>/list.txt, a file a line.
import base64, json, math, os, random, struct, sys
out = sys.argv[1]
repo = sys.argv[2]
os.makedirs(out, exist_ok=True)
rnd = random.Random(11)
files = []
def put(name, data):
    p = os.path.join(out, name)
    with open(p, 'wb') as f: f.write(data if isinstance(data, bytes) else data.encode('utf-8'))
    files.append(name)

class G:
    def __init__(s):
        s.bin = bytearray(); s.views = []; s.acc = []
    def view(s, data, stride=None, pad=4):
        while len(s.bin) % pad: s.bin.append(0)
        off = len(s.bin); s.bin += data
        v = {"buffer": 0, "byteOffset": off, "byteLength": len(data)}
        if stride: v["byteStride"] = stride
        s.views.append(v); return len(s.views) - 1
    def acc_(s, values, typ, comp, n, normalized=False, stride=None, accoff=0):
        fmt = {5120: 'b', 5121: 'B', 5122: 'h', 5123: 'H', 5125: 'I', 5126: 'f'}[comp]
        size = struct.calcsize(fmt)
        count = len(values) // n
        if stride:
            data = bytearray(accoff + stride * count)
            for i in range(count):
                for k in range(n): struct.pack_into('<' + fmt, data, accoff + i * stride + k * size, values[i * n + k])
        else:
            data = bytearray(accoff) + struct.pack('<%d%s' % (len(values), fmt), *values)
        v = s.view(bytes(data), stride)
        a = {"bufferView": v, "componentType": comp, "count": count, "type": typ}
        if accoff: a["byteOffset"] = accoff
        if normalized: a["normalized"] = True
        s.acc.append(a); return len(s.acc) - 1
    def doc(s, extra, uri=True):
        d = {"asset": {"version": "2.0"}, "buffers": [{"byteLength": len(s.bin)}], "bufferViews": s.views, "accessors": s.acc}
        if uri: d["buffers"][0]["uri"] = "data:application/octet-stream;base64," + base64.b64encode(bytes(s.bin)).decode()
        d.update(extra); return d

def box(sx, sy, sz, oy=0.0):
    pos, nrm, idx = [], [], []
    ns = [(1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1)]
    for (nx, ny, nz) in ns:
        u = (0, 0, ny) if abs(ny) > 0.5 else (-nz, 0, nx)
        v = (ny*u[2]-nz*u[1], nz*u[0]-nx*u[2], nx*u[1]-ny*u[0])
        b = len(pos)//3
        for c in [(-1,-1),(1,-1),(1,1),(-1,1)]:
            pos += [(nx+u[0]*c[0]+v[0]*c[1])*sx/2, (ny+u[1]*c[0]+v[1]*c[1])*sy/2+oy, (nz+u[2]*c[0]+v[2]*c[1])*sz/2]
            nrm += [nx, ny, nz]
        idx += [b, b+1, b+2, b, b+2, b+3]
    return pos, nrm, idx

def glb(doc, binary):
    js = json.dumps(doc).encode()
    while len(js) % 4: js += b' '
    b = bytes(binary)
    while len(b) % 4: b += b'\0'
    body = struct.pack('<II', len(js), 0x4E4F534A) + js + struct.pack('<II', len(b), 0x004E4942) + b
    return struct.pack('<III', 0x46546C67, 2, 12 + len(body)) + body

def rand_mesh(g):
    sx, sy, sz = rnd.uniform(0.1, 3), rnd.uniform(0.1, 3), rnd.uniform(0.1, 3)
    pos, nrm, idx = box(sx, sy, sz, rnd.uniform(-1, 1))
    attrs = {}
    comp = rnd.choice([5126, 5126, 5122, 5120, 5121, 5123])
    if comp == 5126: attrs["POSITION"] = g.acc_(pos, "VEC3", 5126, 3, stride=rnd.choice([None, 16, 20]), accoff=rnd.choice([0, 0, 4]))
    else:
        lim = {5122: 32767, 5120: 127, 5121: 255, 5123: 65535}[comp]
        lo = -lim if comp in (5120, 5122) else 0
        q = [max(lo, min(lim, int(x * lim / 3))) for x in pos]
        attrs["POSITION"] = g.acc_(q, "VEC3", comp, 3, normalized=rnd.random() < 0.6, stride=rnd.choice([None, 8 if comp in (5120, 5121) else 12]))
    if rnd.random() < 0.7:
        ncomp = rnd.choice([5126, 5120, 5122])
        if ncomp == 5126: attrs["NORMAL"] = g.acc_(nrm, "VEC3", 5126, 3)
        elif ncomp == 5120: attrs["NORMAL"] = g.acc_([int(x * 127) for x in nrm], "VEC3", 5120, 3, normalized=True, stride=4)
        else: attrs["NORMAL"] = g.acc_([int(x * 32767) for x in nrm], "VEC3", 5122, 3, normalized=True, stride=8)
    if rnd.random() < 0.6:
        uv = [rnd.uniform(-2, 2) for _ in range(len(pos) // 3 * 2)]
        ucomp = rnd.choice([5126, 5121, 5123])
        if ucomp == 5126: attrs["TEXCOORD_0"] = g.acc_(uv, "VEC2", 5126, 2)
        elif ucomp == 5121: attrs["TEXCOORD_0"] = g.acc_([rnd.randrange(256) for _ in uv], "VEC2", 5121, 2, normalized=True)
        else: attrs["TEXCOORD_0"] = g.acc_([rnd.randrange(65536) for _ in uv], "VEC2", 5123, 2, normalized=rnd.random() < 0.5)
    prim = {"attributes": attrs}
    if rnd.random() < 0.8:
        prim["indices"] = g.acc_(idx, "SCALAR", rnd.choice([5123, 5125, 5121]), 1)
    if rnd.random() < 0.7: prim["material"] = rnd.randrange(3)
    prims = [prim]
    if rnd.random() < 0.3: prims.append({"attributes": {"POSITION": attrs["POSITION"]}, "mode": rnd.choice([1, 4, 5])})
    return {"primitives": prims}

def materials():
    m = []
    for i in range(3):
        r = rnd.random()
        if r < 0.2: m.append({})
        elif r < 0.4: m.append({"pbrMetallicRoughness": {}})
        else:
            n = rnd.choice([4, 4, 3, 5])
            m.append({"pbrMetallicRoughness": {"baseColorFactor": [rnd.choice([rnd.uniform(-0.2, 1.3), 0.5, 1.0, 0.0]) for _ in range(n)]}})
    return m

def quat():
    q = [rnd.gauss(0, 1) for _ in range(4)]
    if rnd.random() < 0.7:
        l = math.sqrt(sum(x * x for x in q)); q = [x / l for x in q]
    return q

def rand_gltf():
    g = G()
    nmesh = rnd.randint(1, 4)
    meshes = [rand_mesh(g) for _ in range(nmesh)]
    nn = rnd.randint(1, 8)
    nodes = []
    for i in range(nn):
        nd = {}
        if rnd.random() < 0.8: nd["name"] = rnd.choice(["body", "arm", "Läufer", "a" * 40, "xé中\U0001F600" * 5, "", "n%d" % i])
        if rnd.random() < 0.7: nd["mesh"] = rnd.randrange(nmesh)
        if rnd.random() < 0.25:
            t = [rnd.uniform(-3, 3) for _ in range(3)]; q = quat(); l = math.sqrt(sum(x*x for x in q)); x, y, z, w = [v / l for v in q]
            s = [rnd.uniform(0.2, 2) * rnd.choice([1, 1, -1]) for _ in range(3)]
            R = [[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)], [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)], [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]]
            m = [0.0] * 16
            for c in range(3):
                for rr in range(3): m[c * 4 + rr] = R[rr][c] * s[c]
            m[12], m[13], m[14], m[15] = t[0], t[1], t[2], 1
            nd["matrix"] = m
        else:
            if rnd.random() < 0.6: nd["translation"] = [rnd.uniform(-3, 3) for _ in range(3)]
            if rnd.random() < 0.5: nd["rotation"] = quat()
            if rnd.random() < 0.4: nd["scale"] = [rnd.uniform(0.1, 3) for _ in range(3)]
        nodes.append(nd)
    for i in range(1, nn):
        if rnd.random() < 0.6:
            p = rnd.randrange(i); nodes[p].setdefault("children", []).append(i)
    extra = {"nodes": nodes, "meshes": meshes, "materials": materials()}
    roots = [i for i in range(nn) if not any(i in n.get("children", []) for n in nodes)]
    r = rnd.random()
    if r < 0.5: extra["scenes"] = [{"nodes": roots}]; extra["scene"] = 0
    elif r < 0.7: extra["scenes"] = [{"nodes": roots[:1]}, {"nodes": roots}]; extra["scene"] = 1
    if rnd.random() < 0.7:
        anims = []
        for a in range(rnd.randint(1, 3)):
            samplers, channels = [], []
            for c in range(rnd.randint(1, 4)):
                path = rnd.choice(["translation", "rotation", "scale", "weights"])
                interp = rnd.choice(["LINEAR", "STEP", "CUBICSPLINE", None])
                nk = rnd.randint(1, 6)
                times = sorted(rnd.choice([0, 0.25, 0.5, 1, 1.5, rnd.uniform(0, 2.5)]) for _ in range(nk))
                n = 4 if path == "rotation" else 3
                vals = []
                for _ in range(nk * (3 if interp == "CUBICSPLINE" else 1)):
                    vals += quat() if n == 4 else [rnd.uniform(-2, 2) for _ in range(3)]
                ti = g.acc_(times, "SCALAR", 5126, 1)
                if n == 4 and rnd.random() < 0.3:
                    vi = g.acc_([int(max(-1, min(1, v)) * 32767) for v in vals], "VEC4", 5122, 4, normalized=True)
                else: vi = g.acc_(vals, "VEC4" if n == 4 else "VEC3", 5126, n)
                smp = {"input": ti, "output": vi}
                if interp: smp["interpolation"] = interp
                samplers.append(smp)
                tgt = {"path": path}
                if rnd.random() < 0.95: tgt["node"] = rnd.randrange(nn)
                channels.append({"sampler": len(samplers) - 1, "target": tgt})
            an = {"samplers": samplers, "channels": channels}
            if rnd.random() < 0.7: an["name"] = rnd.choice(["wave", "jump", "", "a very long name of an animation of forty", "Kreisä"])
            anims.append(an)
        extra["animations"] = anims
    return g, extra

for d, _, fs in os.walk(os.path.join(repo, "src/fire.Project")):
    for f in sorted(fs):
        if f.endswith((".gltf", ".glb", ".fmesh", ".fblocks")): put("repo-" + f, open(os.path.join(d, f), 'rb').read())

robot = json.load(open(os.path.join(repo, "src/fire.Project/Samples/Graphics and UI/Scene3D Models/robot.gltf")))
uri = robot["buffers"][0].pop("uri")
robot_bin = base64.b64decode(uri.split(",", 1)[1])
put("robot.glb", glb(robot, robot_bin))
d2 = json.loads(json.dumps(robot)); d2["buffers"][0]["uri"] = "robot%20data.bin"
put("robot data.bin", robot_bin); files.remove("robot data.bin")
put("robot-external.gltf", json.dumps(d2, indent=2))
d3 = json.loads(json.dumps(robot)); d3["buffers"][0]["uri"] = "missing.bin"
put("robot-missing-buffer.gltf", json.dumps(d3))

for k in range(60):
    g, extra = rand_gltf()
    if k % 3 == 0: put("rand%02d.glb" % k, glb(g.doc(extra, uri=False), g.bin))
    else: put("rand%02d.gltf" % k, json.dumps(g.doc(extra), indent=rnd.choice([None, 1])))

def bad(name, d): put(name, d if isinstance(d, (bytes, str)) else json.dumps(d))
g, extra = rand_gltf()
while "meshes" not in extra or not extra["nodes"]: g, extra = rand_gltf()
extra["nodes"][0]["mesh"] = 0
base = g.doc(extra)
def variant(f):
    d = json.loads(json.dumps(base)); f(d); return d
bad("err-noasset.gltf", variant(lambda d: d.pop("asset")))
bad("err-version1.gltf", variant(lambda d: d.__setitem__("asset", {"version": "1.0"})))
bad("err-versionnum.gltf", variant(lambda d: d.__setitem__("asset", {"version": 2})))
bad("err-notjson.gltf", "hello")
bad("err-empty.gltf", "")
bad("err-trailing.gltf", '{"asset": {"version": "2.0"},}')
bad("err-trailing-array.gltf", '{"asset": {"version": "2.0"}, "nodes": [1,]}')
bad("err-comment.gltf", '{"asset": {"version": "2.0"} /* c */}')
bad("err-bom.gltf", b'\xef\xbb\xbf{"asset": {"version": "2.0"}}')
bad("err-array.gltf", '[1, 2]')
bad("err-nonodes.gltf", variant(lambda d: d.pop("nodes")))
bad("err-emptynodes.gltf", variant(lambda d: d.__setitem__("nodes", [])))
bad("err-base64.gltf", variant(lambda d: d["buffers"][0].__setitem__("uri", "data:application/octet-stream;base64,AB$C")))
bad("err-base64pad.gltf", variant(lambda d: d["buffers"][0].__setitem__("uri", "data:application/octet-stream;base64,ABC")))
bad("err-notbase64.gltf", variant(lambda d: d["buffers"][0].__setitem__("uri", "data:application/octet-stream,AAAA")))
bad("err-nobufferdata.gltf", variant(lambda d: d["buffers"][0].pop("uri")))
bad("err-past.gltf", variant(lambda d: d["bufferViews"][0].__setitem__("byteOffset", 10 ** 6)))
bad("err-negoffset.gltf", variant(lambda d: d["bufferViews"][0].__setitem__("byteOffset", -8)))
bad("err-acctype.gltf", variant(lambda d: d["accessors"][0].__setitem__("type", "MAT3")))
bad("err-comptype.gltf", variant(lambda d: d["accessors"][0].__setitem__("componentType", 5130)))
bad("err-count-string.gltf", variant(lambda d: d["accessors"][0].__setitem__("count", "4")))
bad("err-count-frac.gltf", variant(lambda d: d["accessors"][0].__setitem__("count", 1.5)))
bad("err-count-neg.gltf", variant(lambda d: d["accessors"][0].__setitem__("count", -1)))
bad("err-noaccessors.gltf", variant(lambda d: d.pop("accessors")))
bad("err-nomeshes.gltf", variant(lambda d: d.pop("meshes")))
bad("err-noprimitives.gltf", variant(lambda d: d["meshes"][0].pop("primitives")))
bad("err-twice.gltf", variant(lambda d: (d["nodes"][0].setdefault("children", []).append(0), d.pop("scenes", None), d.pop("scene", None))))
bad("err-childoor.gltf", variant(lambda d: (d["nodes"][0].setdefault("children", []).append(77), d.pop("scenes", None), d.pop("scene", None))))
bad("err-sceneoor.gltf", variant(lambda d: (d.__setitem__("scenes", [{"nodes": [0]}]), d.__setitem__("scene", 3))))
bad("err-mesh-oor.gltf", variant(lambda d: d["nodes"][0].__setitem__("mesh", 40)))
bad("err-material-oor.gltf", variant(lambda d: d["meshes"][0]["primitives"][0].__setitem__("material", 40)))
bad("err-buffer-oor.gltf", variant(lambda d: d["bufferViews"][0].__setitem__("buffer", 5)))
bad("err-name-number.gltf", variant(lambda d: d["nodes"][0].__setitem__("name", 5)))
bad("err-normalized-number.gltf", variant(lambda d: d["accessors"][0].__setitem__("normalized", 1)))
bad("err-translation-short.gltf", variant(lambda d: d["nodes"][0].__setitem__("translation", [1, 2])))
bad("err-translation-long.gltf", variant(lambda d: d["nodes"][0].__setitem__("translation", [1, 2, 3, 4])))
bad("err-rotation-short.gltf", variant(lambda d: d["nodes"][0].__setitem__("rotation", [0, 0, 1])))
bad("err-matrix-short.gltf", variant(lambda d: d["nodes"][0].__setitem__("matrix", [1, 0, 0, 0, 0, 1])))
bad("err-color-short.gltf", variant(lambda d: d.__setitem__("materials", [{"pbrMetallicRoughness": {"baseColorFactor": [1, 0]}}] * 3)))
bad("err-color-three.gltf", variant(lambda d: d.__setitem__("materials", [{"pbrMetallicRoughness": {"baseColorFactor": [1, 0, 0.5]}}] * 3)))
def idx_past(d):
    d["accessors"].append({"bufferView": d["accessors"][0]["bufferView"], "componentType": 5125, "count": 1, "type": "SCALAR"})
    d["meshes"][0]["primitives"][0]["indices"] = len(d["accessors"]) - 1
bad("err-index-past.gltf", variant(idx_past))
bad("err-nodes-object.gltf", variant(lambda d: d.__setitem__("nodes", {"a": 1})))
bad("err-scenes-number.gltf", variant(lambda d: d.__setitem__("scenes", 5)))
bad("err-anim-nosamplers.gltf", variant(lambda d: d.__setitem__("animations", [{"channels": []}])))
bad("err-anim-badsampler.gltf", variant(lambda d: d.__setitem__("animations", [{"samplers": [], "channels": [{"sampler": 0, "target": {"node": 0, "path": "rotation"}}]}])))
bad("err-anim-shortvalues.gltf", variant(lambda d: (d["accessors"].append({"componentType": 5126, "count": 2, "type": "SCALAR"}), d.__setitem__("animations", [{"samplers": [{"input": len(d["accessors"]) - 1, "output": len(d["accessors"]) - 1}], "channels": [{"sampler": 0, "target": {"node": 0, "path": "rotation"}}]}]))))
bad("glb-nojson.glb", struct.pack('<III', 0x46546C67, 2, 20) + struct.pack('<II', 0, 0x004E4942))
bad("glb-cut.glb", glb(robot, robot_bin)[:300])
bad("glb-badjson.glb", struct.pack('<III', 0x46546C67, 2, 28) + struct.pack('<II', 8, 0x4E4F534A) + b'{"a": 1,')
bad("glb-notgltf.glb", b'glTX' + b'\0' * 20)

pal_choices = [["#C83232", "#3A9AD9"], ["#ff0000", " #00ff00 ", "##0000FF", "#12345", "zzzzzz", "# ABCDE", "#ABCDEF "], [], None]
def rand_blocks():
    sx, sy, sz = rnd.choice([(16,16,16), (4,6,5), (1,1,1), (3,8,2), (300, 2, 2)])
    d = {"fblocks": 1, "size": [sx, sy, sz]}
    if rnd.random() < 0.7: d["block"] = rnd.choice([0.1, 0.05, 0.25, -1, 0.333])
    pal = rnd.choice(pal_choices)
    if pal is not None: d["palette"] = pal
    np_ = rnd.randint(0, 4)
    if np_:
        parts = []
        for i in range(np_):
            p = {}
            if rnd.random() < 0.9: p["name"] = rnd.choice(["body", "arm", "leg", "a part with a long name of many letters", "Kopfü", None])
            if i > 0 and rnd.random() < 0.8: p["parent"] = rnd.randrange(-1, i)
            if rnd.random() < 0.8: p["pivot"] = [rnd.choice([0, 0.5, 1, 2.25, rnd.randint(0, sx)]) for _ in range(rnd.choice([3, 3, 2, 4]))]
            parts.append(p)
        d["parts"] = parts
    blocks = []
    for _ in range(rnd.randint(0, 60)):
        b = [rnd.randrange(-1, min(sx, 20) + 1), rnd.randrange(-1, sy + 1), rnd.randrange(-1, sz + 1), rnd.randrange(-1, 20)]
        if rnd.random() < 0.5: b.append(rnd.randrange(-1, 6))
        blocks.append(b)
    if rnd.random() < 0.5:
        c = rnd.randrange(4)
        for x in range(min(sx, 4)):
            for y in range(min(sy, 3)):
                for z in range(min(sz, 4)): blocks.append([x, y, z, c])
    d["blocks"] = blocks
    if rnd.random() < 0.7:
        anims = []
        for a in range(rnd.randint(1, 3)):
            an = {}
            if rnd.random() < 0.8: an["name"] = rnd.choice(["wave", "walk", "x" * 40, None])
            if rnd.random() < 0.8: an["length"] = rnd.choice([1, 0.5, 2, 0, -1, 1.27])
            if rnd.random() < 0.6: an["loop"] = rnd.random() < 0.5
            keys = []
            for _ in range(rnd.randint(0, 6)):
                kk = {}
                if rnd.random() < 0.9: kk["part"] = rnd.randrange(0, max(1, np_))
                if rnd.random() < 0.9: kk["time"] = rnd.choice([0, 0.25, 0.5, 0.75, 1, -0.5, rnd.uniform(0, 2)])
                if rnd.random() < 0.8: kk["rotate"] = [rnd.uniform(-180, 180) for _ in range(rnd.choice([3, 3, 1]))]
                if rnd.random() < 0.5: kk["move"] = [rnd.uniform(-3, 3) for _ in range(3)]
                if rnd.random() < 0.4: kk["scale"] = [rnd.uniform(0.5, 2) for _ in range(rnd.choice([3, 2]))]
                keys.append(kk)
            an["keys"] = keys
            anims.append(an)
        d["animations"] = anims
    return d
for k in range(50):
    text = json.dumps(rand_blocks(), indent=rnd.choice([None, 2]))
    if k % 7 == 0: text = "// a block model\n/* c */ " + text
    put("blocks%02d.fblocks" % k, text)
put("blocks-empty.fblocks", "")
put("blocks-blank.fblocks", "  \n\t ")
put("blocks-err-json.fblocks", '{"fblocks": 1, "size": [2, 2')
put("blocks-err-notblocks.fblocks", '{"fmesh": 1}')
put("blocks-err-version.fblocks", '{"fblocks": 2}')
put("blocks-err-versionstr.fblocks", '{"fblocks": "1"}')
put("blocks-err-size.fblocks", '{"fblocks": 1, "size": [2, 2]}')
put("blocks-err-block.fblocks", '{"fblocks": 1, "blocks": [[1, 2, 3]]}')
put("blocks-err-parent.fblocks", '{"fblocks": 1, "parts": [{"name": "a", "parent": 0}]}')
put("blocks-err-type.fblocks", '{"fblocks": 1, "blocks": [[1, 2, 3, "x"]]}')
put("blocks-err-frac.fblocks", '{"fblocks": 1, "blocks": [[1, 2, 3.5, 1]]}')
put("blocks-err-loop.fblocks", '{"fblocks": 1, "animations": [{"loop": 1}]}')
put("blocks-err-array.fblocks", '[1]')
put("blocks-trailing.fblocks", '{"fblocks": 1, "blocks": [[0,0,0,1],],}')
put("blocks-pivot-lazy.fblocks", '{"fblocks": 1, "parts": [{"pivot": [1, 2, 3, "x"]}], "blocks": [[0,0,0,1]]}')

def rand_mesh_model():
    d = {"fmesh": 1}
    pal = rnd.choice(pal_choices)
    if pal is not None: d["palette"] = pal
    np_ = rnd.randint(0, 3)
    if np_:
        d["parts"] = [{"name": rnd.choice(["body", "wheel", "a part with a long name of many letters", "Tür"]), "parent": (rnd.randrange(-1, i) if i else -1), "pivot": [rnd.uniform(-1, 1) for _ in range(3)]} for i in range(np_)]
    verts = [[round(rnd.uniform(-2, 2), rnd.choice([1, 3, 6])) for _ in range(3)] for _ in range(rnd.randint(3, 40))]
    faces = []
    for _ in range(rnd.randint(0, 30)):
        n = rnd.choice([3, 3, 4, 5, 6])
        loop = rnd.sample(range(len(verts)), min(n, len(verts)))
        faces.append([rnd.randrange(-1, 20), rnd.randrange(-1, 5)] + loop)
    verts += [[0, 0, 0], [1, 0, 0], [1, 0, 1], [0, 0, 1], [2, 2, 2]]
    b = len(verts) - 5
    faces.append([1, 0, b, b + 3, b + 2, b + 1])
    faces.append([3, 0, b, b + 1, b])
    d["vertices"] = verts
    d["faces"] = faces
    if rnd.random() < 0.6:
        d["animations"] = [{"name": "spin", "length": rnd.choice([1, 2.5]), "loop": rnd.random() < 0.5, "keys": [{"part": rnd.randrange(max(1, np_)), "time": t, "rotate": [0, t * 90, 0], "move": [t, 0, 0]} for t in (0, 0.5, 1)]}]
    return d
for k in range(40):
    put("mesh%02d.fmesh" % k, json.dumps(rand_mesh_model(), indent=rnd.choice([None, 2])))
put("mesh-empty.fmesh", "")
put("mesh-err-notmesh.fmesh", '{"fblocks": 1}')
put("mesh-err-corner.fmesh", '{"fmesh": 1, "vertices": [[1, 2]]}')
put("mesh-err-face.fmesh", '{"fmesh": 1, "vertices": [[1, 2, 3]], "faces": [[0, 0, 0, 0]]}')
put("mesh-err-faceoor.fmesh", '{"fmesh": 1, "vertices": [[1, 2, 3]], "faces": [[0, 0, 0, 0, 5]]}')
put("mesh-err-json.fmesh", '{"fmesh": 1, "vertices": [[1, 2, 3]], }x')
put("mesh-comments.fmesh", '// c\n{"fmesh": 1, /* x */ "vertices": [[0,0,0],[1,0,0],[0,1,0],], "faces": [[0,0,0,1,2],],}')

texts = [f for f in files if f.endswith((".gltf", ".fblocks", ".fmesh")) and not f.startswith(("err-", "blocks-err", "mesh-err"))]
for i, f in enumerate(texts[:90]):
    data = open(os.path.join(out, f), 'rb').read()
    if len(data) < 4: continue
    r = random.Random(i)
    ext = os.path.splitext(f)[1]
    a = r.randrange(len(data)); put("mut%02da%s" % (i, ext), data[:a])
    a = r.randrange(len(data)); put("mut%02db%s" % (i, ext), data[:a] + data[a + 1:])
    a = r.randrange(len(data)); b = min(len(data), a + r.randrange(1, 12)); put("mut%02dc%s" % (i, ext), data[:b] + data[a:])
globs = [f for f in files if f.endswith(".glb")]
for i, f in enumerate(globs):
    data = bytearray(open(os.path.join(out, f), 'rb').read())
    r = random.Random(100 + i)
    for j in range(3):
        d = bytearray(data)
        for _ in range(r.randint(1, 4)): d[r.randrange(len(d))] = r.randrange(256)
        put("mut-glb%02d%s.glb" % (i, "abc"[j]), bytes(d))
with open(os.path.join(out, "list.txt"), "w") as f:
    for n in files: f.write(n + "\n")
print(len(files))
