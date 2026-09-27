"""GPU smoke test for the gfx1201 gsplat build: fit 3DGS and 2DGS splats to a target image.

Independent of the port's own self-consistency checks: if forward or backward were wrong, the
photometric loss would not fall. Run:  /workspace/envs/gsplat/bin/python gsplat_smoke.py
"""

import math
import time

import torch
from gsplat import rasterization, rasterization_2dgs

torch.manual_seed(0)
dev = "cuda"
W = H = 128
K = torch.tensor([[100.0, 0, W / 2], [0, 100.0, H / 2], [0, 0, 1]], device=dev)[None]
viewmat = torch.eye(4, device=dev)[None]


def splats(n):
    return {
        "means": torch.cat(
            [torch.rand(n, 2, device=dev) * 1.6 - 0.8, torch.rand(n, 1, device=dev) + 2.0], 1
        ),
        "quats": torch.nn.functional.normalize(torch.randn(n, 4, device=dev), dim=-1),
        "scales": torch.full((n, 3), math.log(0.04), device=dev),
        "opacities": torch.zeros(n, device=dev),
        "colors": torch.rand(n, 3, device=dev),
    }


def render(fn, p):
    out = fn(
        p["means"],
        p["quats"],
        torch.exp(p["scales"]),
        torch.sigmoid(p["opacities"]),
        p["colors"],
        viewmat,
        K,
        W,
        H,
    )
    return out[0][0, ..., :3]


def fit(fn, steps=300):
    with torch.no_grad():
        target = render(fn, splats(200)).clamp(0, 1)
    p = {k: v.requires_grad_() for k, v in splats(400).items()}
    opt = torch.optim.Adam(p.values(), lr=1e-2)
    for i in range(steps):
        loss = torch.nn.functional.mse_loss(render(fn, p), target)
        opt.zero_grad()
        loss.backward()
        opt.step()
        if i == 0:
            first = loss.item()
    psnr = lambda m: -10 * math.log10(m)
    return psnr(first), psnr(loss.item())


if __name__ == "__main__":
    print(torch.__version__, torch.cuda.get_device_name(0))
    ok = True
    for name, fn in [("3DGS", rasterization), ("2DGS", rasterization_2dgs)]:
        t = time.time()
        a, b = fit(fn)
        good = b > a + 8
        ok &= good
        print(
            f"{name} fit: PSNR {a:.1f} -> {b:.1f} dB in {time.time() - t:.1f}s",
            "PASS" if good else "FAIL",
        )
    print("GSPLAT_SMOKE:", "PASS" if ok else "FAIL")
