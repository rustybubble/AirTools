"""GPU smoke test for the original 2DGS extensions (diff_surfel_rasterization, simple_knn) on
gfx1201: simple_knn on a known grid, then fit 2D surfels to a target image through the rasteriser.
Run with the 2DGS source tree on PYTHONPATH (for utils.graphics_utils):
  PYTHONPATH=/workspace/src/2dgs-rocm /workspace/envs/2dgs/bin/python surfel_smoke.py
"""

import math

import torch
from diff_surfel_rasterization import GaussianRasterizationSettings, GaussianRasterizer
from simple_knn._C import distCUDA2
from utils.graphics_utils import getProjectionMatrix

torch.manual_seed(0)
dev = "cuda"

# simple_knn: mean squared distance to the 3 nearest neighbours; on a unit grid it is exactly 1.
g = torch.stack(torch.meshgrid(*[torch.arange(10.0)] * 3, indexing="ij"), -1).reshape(-1, 3)
d = distCUDA2(g.to(dev))
knn_ok = abs(d.median().item() - 1.0) < 1e-4
print(
    f"simple_knn distCUDA2 on unit grid: median={d.median().item():.4f}",
    "PASS" if knn_ok else "FAIL",
)

W = H = 128
fov = 2 * math.atan(W / 2 / 100.0)
view = torch.eye(4, device=dev)
proj = getProjectionMatrix(znear=0.01, zfar=100, fovX=fov, fovY=fov).T.to(dev)
settings = GaussianRasterizationSettings(
    image_height=H,
    image_width=W,
    tanfovx=math.tan(fov / 2),
    tanfovy=math.tan(fov / 2),
    bg=torch.zeros(3, device=dev),
    scale_modifier=1.0,
    viewmatrix=view,
    projmatrix=view @ proj,
    sh_degree=0,
    campos=torch.zeros(3, device=dev),
    prefiltered=False,
    debug=False,
)
raster = GaussianRasterizer(settings)


def splats(n):
    return {
        "means": torch.cat(
            [torch.rand(n, 2, device=dev) * 1.6 - 0.8, torch.rand(n, 1, device=dev) + 2.0], 1
        ),
        "scales": torch.full((n, 2), math.log(0.04), device=dev),
        "rots": torch.nn.functional.normalize(torch.randn(n, 4, device=dev), dim=-1),
        "opac": torch.zeros(n, 1, device=dev),
        "rgb": torch.rand(n, 3, device=dev),
    }


def render(p):
    m2d = torch.zeros_like(p["means"], requires_grad=True)
    img, radii, allmap = raster(
        means3D=p["means"],
        means2D=m2d,
        opacities=torch.sigmoid(p["opac"]),
        colors_precomp=p["rgb"],
        scales=torch.exp(p["scales"]),
        rotations=p["rots"],
    )
    return img


with torch.no_grad():
    target = render(splats(200)).clamp(0, 1)
p = {k: v.requires_grad_() for k, v in splats(400).items()}
opt = torch.optim.Adam(p.values(), lr=1e-2)
for i in range(300):
    loss = torch.nn.functional.mse_loss(render(p), target)
    opt.zero_grad()
    loss.backward()
    opt.step()
    first = loss.item() if i == 0 else first
a, b = -10 * math.log10(first), -10 * math.log10(loss.item())
fit_ok = b > a + 8
print(f"diff_surfel_rasterization fit: PSNR {a:.1f} -> {b:.1f} dB", "PASS" if fit_ok else "FAIL")
# depth: camera-facing surfels at z=2 must render depth 2 (expected depth / alpha, and median depth)
n = 50
means = torch.cat([torch.rand(n, 2, device=dev) * 1.2 - 0.6, torch.full((n, 1), 2.0, device=dev)], 1)
_, _, allmap = raster(
    means3D=means, means2D=torch.zeros_like(means), opacities=torch.full((n, 1), 0.99, device=dev),
    colors_precomp=torch.rand(n, 3, device=dev), scales=torch.full((n, 2), 0.1, device=dev),
    rotations=torch.tensor([[1.0, 0, 0, 0]], device=dev).repeat(n, 1),
)
m = allmap[1] > 0.5
dexp, dmed = (allmap[0][m] / allmap[1][m]).median().item(), allmap[5][m].median().item()
depth_ok = abs(dexp - 2) < 1e-3 and abs(dmed - 2) < 1e-3
print(f"surfel depth at z=2: expected {dexp:.4f}, median {dmed:.4f}", "PASS" if depth_ok else "FAIL")
print("SURFEL_SMOKE:", "PASS" if knn_ok and fit_ok and depth_ok else "FAIL")
