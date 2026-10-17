#!/usr/bin/env python3
"""Read-only Unity serialized-asset inventory; JSON summaries stay outside the repository.

Requires UnityPy in an external venv. Never exports game object payloads.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import struct
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
import tldenv

DATA = tldenv.GAME_DIR / "TheLongDrive_Data"
CACHE = tldenv.CACHE / "assets"
FILES = ("globalgamemanagers", "globalgamemanagers.assets", "resources.assets", "sharedassets0.assets", "level0")
COLLIDERS = {"BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider", "WheelCollider", "TerrainCollider"}


def ptr(p):
    return int(getattr(p, "m_PathID", 0)) if int(getattr(p, "m_FileID", 0)) == 0 else 0


def component_script(obj, names, errors):
    # Native MonoBehaviour header is m_GameObject PPtr, m_Enabled, m_Script PPtr.
    # Read only the fixed header when custom script typetrees cannot be decoded.
    try:
        raw = obj.get_raw_data()
        if len(raw) < 28:
            raise ValueError("short MonoBehaviour header")
        gofile, goid = struct.unpack_from("<iq", raw, 0)
        scriptfile, scriptid = struct.unpack_from("<iq", raw, 16)
        name = names.get(scriptid) if scriptfile == 1 else None
        return (goid if gofile == 0 else 0), name, scriptfile, scriptid
    except Exception as exc:
        errors[type(exc).__name__ + ": " + str(exc)[:120]] += 1
        return 0, None, -1, 0


def scan(data):
    import UnityPy
    paths = [data / name for name in FILES]
    for path in paths:
        if not path.is_file():
            raise FileNotFoundError(path)
    env = UnityPy.load(*(str(path) for path in paths))
    files = {Path(k).name: v for k, v in env.files.items()}
    errors = Counter()
    names = {}
    for obj in files["globalgamemanagers.assets"].objects.values():
        if obj.type.name == "MonoScript":
            try:
                s = obj.read()
                names[obj.path_id] = s.m_ClassName or s.m_Name
            except Exception as exc:
                errors["MonoScript " + type(exc).__name__] += 1
    result = {
        "tool": "UnityPy", "version": UnityPy.__version__,
        "input": {x.name: {"bytes": x.stat().st_size, "sha256": hashlib.sha256(x.read_bytes()).hexdigest()} for x in paths},
        "build_scenes": [], "files": {}, "mesh_top": [], "material_shader": {},
        "roots": [], "lights": {}, "errors": {},
        "limitations": ["Serialized counts are object definitions, not runtime instance or draw-call counts.",
                        "MonoBehaviour script names are resolved through script references; custom script fields are not decoded.",
                        "Hierarchy represents serialized roots only; runtime instantiated prefabs and streaming counts are unknown."],
    }
    for obj in files["globalgamemanagers"].objects.values():
        if obj.type.name == "BuildSettings":
            try:
                result["build_scenes"] = obj.read().scenes
            except Exception as exc:
                errors["BuildSettings " + type(exc).__name__] += 1
    script_totals = Counter()
    root_rows = []
    mesh_rows = []
    mat_shader = Counter()
    light_types = Counter()
    light_shadows = Counter()
    renderer_shadows = Counter()
    for filename in ("sharedassets0.assets", "level0"):
        f = files[filename]
        objects = f.objects
        counts = Counter(obj.type.name for obj in objects.values())
        go_names = {}
        comps = defaultdict(Counter)
        transforms = {}
        parents = {}
        lod = {}
        referenced_meshes = Counter()
        for obj in objects.values():
            typ = obj.type.name
            try:
                if typ == "GameObject":
                    value = obj.read()
                    go_names[obj.path_id] = value.m_Name
                    comps[obj.path_id]["GameObject"] += 1
                    for pair in value.m_Component:
                        co = objects.get(ptr(pair.component))
                        if co:
                            comps[obj.path_id][co.type.name] += 1
                elif typ in ("Transform", "RectTransform"):
                    value = obj.read()
                    transforms[obj.path_id] = ptr(value.m_GameObject)
                    parents[obj.path_id] = ptr(value.m_Father)
                elif typ == "MonoBehaviour":
                    go, script, ext, scriptid = component_script(obj, names, errors)
                    if script:
                        script_totals[script] += 1
                        if go:
                            comps[go]["script:" + script] += 1
                    elif ext == 1:
                        errors["unresolved script reference"] += 1
                elif typ == "LODGroup":
                    value = obj.read()
                    lod[ptr(value.m_GameObject)] = len(value.m_LODs)
                elif typ == "Mesh":
                    value = obj.read()
                    mesh_rows.append({"file": filename, "name": value.m_Name, "path_id": obj.path_id,
                                      "vertices": value.m_VertexData.m_VertexCount,
                                      "triangle_indices": sum(s.indexCount for s in value.m_SubMeshes if s.topology == 0),
                                      "submeshes": len(value.m_SubMeshes), "serialized_bytes": obj.byte_size,
                                      "stream_bytes": value.m_StreamData.size})
                elif typ == "MeshFilter":
                    value = obj.read()
                    referenced_meshes[ptr(value.m_Mesh)] += 1
                elif typ == "Light":
                    value = obj.read()
                    light_types[str(value.m_Type)] += 1
                    light_shadows[str(value.m_Shadows.m_Type)] += 1
                elif typ in ("MeshRenderer", "SkinnedMeshRenderer"):
                    value = obj.read()
                    renderer_shadows[str(value.m_CastShadows)] += 1
            except Exception as exc:
                errors[typ + " " + type(exc).__name__ + ": " + str(exc)[:100]] += 1
        # Link transform parent GO to child GO. Scene and sharedassets hierarchies
        # are deliberately separate; do not guess references across files.
        children = defaultdict(list)
        for t, go in transforms.items():
            parent_go = transforms.get(parents[t], 0)
            if parent_go and parent_go != go:
                children[parent_go].append(go)
        roots = sorted(go for go in go_names if go not in {child for siblings in children.values() for child in siblings})
        for root in roots:
            stack = [root]
            visited = set()
            totals = Counter()
            while stack:
                go = stack.pop()
                if go in visited:
                    continue
                visited.add(go)
                totals.update(comps[go])
                if go in lod:
                    totals["LODGroup_levels"] += lod[go]
                stack.extend(children[go])
            row = {"file": filename, "name": go_names[root], "path_id": root,
                   "descendants": len(visited) - 1, "components": dict(totals)}
            root_rows.append(row)
        result["files"][filename] = {"objects": len(objects), "type_counts": dict(counts),
                                      "roots": len(roots), "named_gameobjects": len(go_names),
                                      "referenced_meshes": len(referenced_meshes)}
    for filename in ("resources.assets", "globalgamemanagers.assets"):
        f = files[filename]
        result["files"][filename] = {"objects": len(f.objects), "type_counts": dict(Counter(o.type.name for o in f.objects.values()))}
    for filename in ("sharedassets0.assets", "resources.assets", "globalgamemanagers.assets"):
        f = files[filename]
        for obj in f.objects.values():
            if obj.type.name != "Material":
                continue
            try:
                material = obj.read()
                shader = material.m_Shader
                sid = int(shader.m_PathID)
                if int(shader.m_FileID) == 0:
                    shader_obj = f.objects.get(sid)
                else:
                    external = f.externals[int(shader.m_FileID) - 1]
                    shader_obj = files.get(Path(external.path).name)
                    shader_obj = shader_obj.objects.get(sid) if shader_obj else None
                shader_name = shader_obj.read().m_ParsedForm.m_Name if shader_obj and shader_obj.type.name == "Shader" else "<external/unresolved>"
                mat_shader[shader_name] += 1
            except Exception as exc:
                errors["Material/Shader " + type(exc).__name__ + ": " + str(exc)[:100]] += 1
    result["material_shader"] = dict(mat_shader.most_common())
    result["script_counts"] = dict(script_totals.most_common())
    result["mesh_top"] = sorted(mesh_rows, key=lambda m: m["triangle_indices"], reverse=True)[:25]
    result["mesh_totals"] = {"decoded": len(mesh_rows), "vertices": sum(m["vertices"] for m in mesh_rows),
                             "triangle_indices": sum(m["triangle_indices"] for m in mesh_rows),
                             "stream_bytes": sum(m["stream_bytes"] for m in mesh_rows)}
    result["lights"] = {"types_numeric": dict(light_types), "shadow_types_numeric": dict(light_shadows),
                         "renderer_cast_shadow_numeric": dict(renderer_shadows)}
    result["roots"] = root_rows
    result["errors"] = dict(errors)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=DATA, help="read-only Unity data directory")
    parser.add_argument("--output", type=Path, default=CACHE / "scan.json", help="JSON destination outside git")
    args = parser.parse_args()
    if args.output.resolve().is_relative_to(Path(__file__).resolve().parents[1]):
        parser.error("output must stay outside the git worktree")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    result = scan(args.data)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(f"Scanned {len(result['roots'])} roots; {result['mesh_totals']['decoded']} meshes; output: {args.output}")
    print(f"Parse/identity issues: {sum(result['errors'].values())}")


if __name__ == "__main__":
    main()
