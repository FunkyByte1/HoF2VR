#!/usr/bin/env python3
"""Turn on Unity's built-in VR (OpenVR device) and the Steam Audio spatializer in Hand of Fate 2's data.unity3d, surgically.

  patch_vr.py [--no-hrtf] <original data.unity3d> <output data.unity3d> [device ...]   (default device: OpenVR)

What changes, and nothing else (all in globalgamemanagers):
  * BuildSettings.enabledVRDevices: empty -> devices.
  * (unless --no-hrtf; docs/hrtf-plan.md) BuildSettings.preloadedPlugins += "audioplugin_phonon" (Unity registers the
    plugin's audio effects at boot) and AudioManager.m_SpatializerPlugin = "Steam Audio Spatializer" (the spatializer a
    source with AudioSource.spatialize set gets). --no-hrtf gives the VR-only file of before, byte for byte.
  Each object's new bytes come from UnityPy's serializer, which is first proven byte-exact on the unchanged object. The
  edits are applied in file order, the object table re-read after each: objects after an edited one shift by an
  8-byte-aligned delta; only their byteStart fields and the file-size header change.
  In the UnityFS bundle, every original (compressed) block is copied verbatim. The patched globalgamemanagers is appended
  as new LZ4 blocks and its directory entry re-pointed.
Verification re-reads the output with an independent reader and compares RAW bytes of every file in the bundle
(identical except globalgamemanagers), checks every object in the new globalgamemanagers is byte-identical to the
original except the edited ones, and re-reads the edited objects with UnityPy (the new values; every other field equal).
Run with shared/tools-venv/bin/python (needs UnityPy, lz4).
"""
import io
import struct
import sys

import lz4.block
import UnityPy


# ---------------------------------------------------------------- UnityFS (version 6)
def read_bundle(path):
    d = open(path, "rb").read()
    if not d.startswith(b"UnityFS\0"):
        raise SystemExit("not a UnityFS bundle")
    p = 8
    version = struct.unpack_from(">I", d, p)[0]
    p += 4
    if version != 6:
        raise SystemExit("unsupported UnityFS version %d" % version)
    strings_start = p
    for _ in range(2):
        p = d.index(b"\0", p) + 1
    strings = d[strings_start:p]
    size, csz, usz, flags = struct.unpack_from(">QIII", d, p)
    p += 20
    if flags & 0x80:
        raise SystemExit("block info at end: unsupported")
    bi_raw = d[p:p + csz]
    bi = decompress(bi_raw, usz, flags & 0x3F)
    data_start = p + csz
    h = bi[:16]
    n = struct.unpack_from(">I", bi, 16)[0]
    blocks = [struct.unpack_from(">IIH", bi, 20 + 10 * i) for i in range(n)]
    q = 20 + 10 * n
    m = struct.unpack_from(">I", bi, q)[0]
    q += 4
    nodes = []
    for _ in range(m):
        off, sz, fl = struct.unpack_from(">QQI", bi, q)
        q += 20
        e = bi.index(b"\0", q)
        nodes.append([bi[q:e].decode(), off, sz, fl])
        q = e + 1
    # decompress the data stream
    stream = bytearray()
    cp = data_start
    for u, c, f in blocks:
        stream += decompress(d[cp:cp + c], u, f & 0x3F)
        cp += c
    return dict(raw=d, strings=strings, flags=flags, hash=h, blocks=blocks, nodes=nodes,
                data_start=data_start, data_end=cp, stream=bytes(stream))


def decompress(buf, usize, kind):
    if kind == 0:
        return bytes(buf)
    if kind in (2, 3):
        return lz4.block.decompress(buf, uncompressed_size=usize)
    raise SystemExit("unsupported compression %d" % kind)


def node_bytes(b, name):
    for nm, off, sz, fl in b["nodes"]:
        if nm == name:
            return b["stream"][off:off + sz]
    raise KeyError(name)


def write_bundle(orig, new_files):
    """Original blocks verbatim + appended blocks holding new_files {name: bytes}."""
    blocks = list(orig["blocks"])
    extra = bytearray()
    nodes = [list(x) for x in orig["nodes"]]
    offset = sum(u for u, c, f in orig["blocks"])
    for name, content in new_files.items():
        for node in nodes:
            if node[0] == name:
                node[1], node[2] = offset, len(content)
                break
        else:
            raise SystemExit("node not found: " + name)
        for i in range(0, len(content), 0x20000):
            chunk = content[i:i + 0x20000]
            comp = lz4.block.compress(chunk, mode="high_compression", store_size=False)
            blocks.append((len(chunk), len(comp), 2))
            extra += comp
        offset += len(content)
    bi = bytearray(orig["hash"])
    bi += struct.pack(">I", len(blocks))
    for u, c, f in blocks:
        bi += struct.pack(">IIH", u, c, f)
    bi += struct.pack(">I", len(nodes))
    for name, off, sz, fl in nodes:
        bi += struct.pack(">QQI", off, sz, fl) + name.encode() + b"\0"
    bi = bytes(bi)
    bic = lz4.block.compress(bi, mode="high_compression", store_size=False)
    body = orig["raw"][orig["data_start"]:orig["data_end"]] + bytes(extra)
    head_len = 8 + 4 + len(orig["strings"]) + 20
    total = head_len + len(bic) + len(body)
    head = b"UnityFS\0" + struct.pack(">I", 6) + orig["strings"] + \
        struct.pack(">QIII", total, len(bic), len(bi), (orig["flags"] & ~0x3F) | 3)
    out = head + bic + body
    assert len(out) == total
    return out


# ---------------------------------------------------------------- SerializedFile (version 17)
def object_table(sf):
    """Returns (header fields, list of objects with file positions of byteStart/byteSize)."""
    meta_size, file_size, version, data_offset = struct.unpack_from(">IIII", sf, 0)
    if version != 17:
        raise SystemExit("unsupported SerializedFile version %d" % version)
    if sf[16] != 0:
        raise SystemExit("big-endian data unsupported")
    r = io.BytesIO(sf)
    r.seek(20)

    def cstr():
        s = b""
        while True:
            ch = r.read(1)
            if ch == b"\0":
                return s
            s += ch

    def u8():
        return r.read(1)[0]

    def i16():
        return struct.unpack("<h", r.read(2))[0]

    def i32():
        return struct.unpack("<i", r.read(4))[0]

    cstr()  # unity version
    i32()  # target platform
    type_tree = u8()
    if type_tree:
        raise SystemExit("type trees present: unsupported by this small parser")
    for _ in range(i32()):
        cls = i32()
        u8()  # stripped
        i16()  # script type index
        if cls == 114:
            r.read(16)
        r.read(16)
    objs = []
    for _ in range(i32()):
        pos = r.tell()
        r.seek((pos + 3) & ~3)
        path_id = struct.unpack("<q", r.read(8))[0]
        start_pos = r.tell()
        start, size = struct.unpack("<II", r.read(8))
        type_id = i32()
        objs.append(dict(path_id=path_id, start=start, size=size, type_id=type_id, field_pos=start_pos))
    return dict(meta_size=meta_size, file_size=file_size, data_offset=data_offset), objs


def patch_ggm(ggm, new_obj_bytes, path_id):
    hdr, objs = object_table(ggm)
    objs_sorted = sorted(objs, key=lambda o: o["start"])
    for a, b in zip(objs_sorted, objs_sorted[1:]):
        if a["start"] + a["size"] > b["start"]:
            raise SystemExit("overlapping objects")
    target = next(o for o in objs if o["path_id"] == path_id)
    t_start = hdr["data_offset"] + target["start"]
    old_end = t_start + target["size"]
    new_end = t_start + len(new_obj_bytes)
    shift = ((new_end + 7) & ~7) - ((old_end + 7) & ~7)
    out = bytearray(ggm[:t_start]) + new_obj_bytes
    out += b"\0" * (((new_end + 7) & ~7) - new_end)
    out += ggm[(old_end + 7) & ~7:]
    # patch table entries (they live in the metadata, before data_offset, so positions are stable)
    struct.pack_into("<I", out, target["field_pos"] + 4, len(new_obj_bytes))
    for o in objs:
        if o["start"] > target["start"]:
            struct.pack_into("<I", out, o["field_pos"], o["start"] + shift)
    struct.pack_into(">I", out, 4, hdr["file_size"] + shift)
    if hdr["file_size"] != len(ggm):
        raise SystemExit("unexpected file size header")
    return bytes(out), shift


HRTF_PLUGIN = "audioplugin_phonon"            # <Data>/Plugins/x86_64/libaudioplugin_phonon.so
HRTF_SPATIALIZER = "Steam Audio Spatializer"   # the plugin's spatializer effect


def ggm_object(env, type_name):
    return next(o for o in env.objects if o.type.name == type_name and o.assets_file.name == "globalgamemanagers")


def main():
    args = sys.argv[1:]
    hrtf = "--no-hrtf" not in args
    args = [a for a in args if a != "--no-hrtf"]
    if len(args) < 2:
        raise SystemExit(__doc__)
    src, dst = args[0], args[1]
    devices = args[2:] or ["OpenVR"]

    orig = read_bundle(src)
    ggm = node_bytes(orig, "globalgamemanagers")
    env = UnityPy.load(src)

    # the edits: {path_id: (object, original tree, new tree, new bytes)}
    edits = {}
    plan = [("BuildSettings", {"enabledVRDevices": devices})]
    if hrtf:
        bs_tree = ggm_object(env, "BuildSettings").read_typetree()
        plugins = list(bs_tree["preloadedPlugins"])
        if HRTF_PLUGIN not in plugins:
            plugins.append(HRTF_PLUGIN)
        plan[0][1]["preloadedPlugins"] = plugins
        plan.append(("AudioManager", {"m_SpatializerPlugin": HRTF_SPATIALIZER}))
    for type_name, changes in plan:
        obj = ggm_object(env, type_name)
        raw = obj.get_raw_data()
        tree = obj.read_typetree()
        # save_typetree() returns the serialized bytes (get_raw_data() always re-reads the original)
        if bytes(obj.save_typetree(tree)) != raw:
            raise SystemExit("%s: serializer round-trip is not byte-exact; refusing" % type_name)
        print("%s: serializer round-trip byte-exact (%d bytes)" % (type_name, len(raw)))
        new_tree = dict(tree)
        for k, v in changes.items():
            if k not in tree:
                raise SystemExit("%s has no field %s" % (type_name, k))
            print("  %s: %r -> %r" % (k, tree[k], v))
            new_tree[k] = v
        new_obj = bytes(obj.save_typetree(new_tree))
        print("  %s: %d -> %d bytes" % (type_name, len(raw), len(new_obj)))
        edits[obj.path_id] = (obj, tree, new_tree, new_obj, type_name)

    # apply in file order; patch_ggm re-reads the object table of the bytes it is given each time
    h0, o0 = object_table(ggm)
    starts = {o["path_id"]: o["start"] for o in o0}
    new_ggm = ggm
    for pid in sorted(edits, key=lambda p: starts[p]):
        new_ggm, shift = patch_ggm(new_ggm, edits[pid][3], pid)
        print("globalgamemanagers: %s patched, objects after it shift %d" % (edits[pid][4], shift))
    print("globalgamemanagers: %d -> %d bytes" % (len(ggm), len(new_ggm)))
    out = write_bundle(orig, {"globalgamemanagers": new_ggm})
    with open(dst, "wb") as f:
        f.write(out)
    print("wrote", dst, len(out), "bytes")

    # ---- verification (independent reader, raw bytes)
    chk = read_bundle(dst)
    for name, off, sz, fl in orig["nodes"]:
        a = node_bytes(orig, name)
        b = node_bytes(chk, name)
        if name == "globalgamemanagers":
            if b != new_ggm:
                raise SystemExit("globalgamemanagers in output differs from the patched bytes")
        elif a != b:
            raise SystemExit("raw bytes changed for " + name)
    print("raw check: all %d files identical except globalgamemanagers" % (len(orig["nodes"]) - 1))
    h1, o1 = object_table(new_ggm)
    if [o["path_id"] for o in o0] != [o["path_id"] for o in o1]:
        raise SystemExit("object table order changed")
    for a, b in zip(o0, o1):
        ra = ggm[h0["data_offset"] + a["start"]:h0["data_offset"] + a["start"] + a["size"]]
        rb = new_ggm[h1["data_offset"] + b["start"]:h1["data_offset"] + b["start"] + b["size"]]
        if a["path_id"] in edits:
            if rb != edits[a["path_id"]][3]:
                raise SystemExit("%s bytes wrong in output" % edits[a["path_id"]][4])
        elif ra != rb:
            raise SystemExit("object %d changed" % a["path_id"])
    print("object check: %d objects identical except %s" % (len(o0) - len(edits), ", ".join(e[4] for e in edits.values())))
    env2 = UnityPy.load(dst)
    for pid, (obj, tree, new_tree, new_obj, type_name) in edits.items():
        t2 = ggm_object(env2, type_name).read_typetree()
        if any(t2[k] != new_tree[k] for k in new_tree) or set(t2) != set(new_tree):
            raise SystemExit("%s re-read mismatch" % type_name)
        changed = {k: t2[k] for k in t2 if t2[k] != tree[k]}
        print("after:  %s %s (UnityPy re-read OK; every other field unchanged)" % (type_name, changed))


if __name__ == "__main__":
    main()
