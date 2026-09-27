"""Run LLM-written CadQuery code and write a contract-frame GLB (docs/api.md §2).

The code is written in the usual CAD frame (mm, Z up, product front facing -Y, standard CadQuery
"front" plane): back/mounting face on Y=0, product in -d<=Y<=0, -w/2<=X<=w/2, 0<=Z<=h. It must
define `parts = [(shape_or_workplane, "#RRGGBB"), ...]` (or a single `result`).

Mapping to glTF: (x, y, z)_cad -> (x, z - h/2, -y) / 1000, a proper rotation (the standard Z-up to
Y-up swap) plus the mount-face-centre origin. Uses the declared h, not the bbox, so a wrong-size
model stays wrong and the scorer sees it.

Runs in an env with cadquery + trimesh (not a project dependency):
    work/assets-llm/envs/cq/bin/python -m server.experiments.assets_llm.cq_to_glb code.py out.glb H_MM
"""

import sys
import tempfile
from pathlib import Path

import cadquery as cq
import numpy as np
import trimesh


def _material(hex_color: str) -> trimesh.visual.TextureVisuals:
    h = hex_color.lstrip("#")
    srgb = [int(h[i : i + 2], 16) / 255 for i in (0, 2, 4)]
    # glTF baseColorFactor is linear; hex colours are sRGB
    rgba = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb] + [1.0]
    return trimesh.visual.TextureVisuals(
        material=trimesh.visual.material.PBRMaterial(
            baseColorFactor=rgba, metallicFactor=0.1, roughnessFactor=0.6
        )
    )


def code_to_glb(code: str, out: Path, h_mm: float) -> dict:
    ns: dict = {"cq": cq}
    exec(compile(code, "<llm>", "exec"), ns)  # noqa: S102 -- sandboxing is the caller's job
    parts = ns.get("parts") or [(ns["result"], "#B0B0B0")]
    to_gltf = np.array([[1, 0, 0, 0], [0, 0, 1, -h_mm / 2], [0, -1, 0, 0], [0, 0, 0, 1.0]])
    scene = trimesh.Scene()
    with tempfile.TemporaryDirectory() as tmp:
        for i, (obj, color) in enumerate(parts):
            stl = Path(tmp) / f"{i}.stl"
            cq.exporters.export(obj, str(stl), tolerance=0.2, angularTolerance=0.2)
            mesh = trimesh.load(stl, force="mesh")
            mesh.apply_transform(to_gltf)
            mesh.apply_scale(0.001)
            mesh.visual = _material(color)
            scene.add_geometry(mesh, node_name=f"part{i}")
    scene.export(out)
    return {"parts": len(parts), "triangles": sum(len(g.faces) for g in scene.geometry.values())}


if __name__ == "__main__":
    src, dst, h = sys.argv[1], sys.argv[2], float(sys.argv[3])
    print(code_to_glb(Path(src).read_text(), Path(dst), h))
