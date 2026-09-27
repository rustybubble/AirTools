"""E6: 2DGS (gsplat `rasterization_2dgs`, gfx1201 port) on the undistorted COLMAP frames, then
per-view depth renders for E3's TSDF + TextureMesh tail (docs/research/bench/e6-splat-mesh.md).

Runs INSIDE the gsplat venv (`/workspace/envs/gsplat`: ROCm torch + gsplat; no cv2/scipy/open3d),
standalone like `depthfusion_worker.py`. Inputs are E3's `cameras.npz` (`pipeline.depthfusion.
export_cameras`: K, cam-from-world, sparse observations) and `dense/images`. The recipe follows
gsplat's `examples/simple_trainer_2dgs.py` (L1 + 0.2 D-SSIM, DefaultStrategy on `gradient_2dgs`,
normal-consistency and depth-distortion regularisers), with `sh_degree 0` (SH>0 is unvalidated on
the port) and optional DN-Splatter-style mono priors (`--priors`, from `priors.py`): log-L1 on the
expected depth and 1-cos on the rendered normals.

At every `--save-at` step (and the last) it writes, under `out/step<k>/`:
- `depth/<frame>.npz` (`d`: median depth in COLMAP units at the training size, 0 where alpha <
  0.5): the drop-in for depthfusion's depth cache;
- `eval/<id>.png` + `eval/<id>_a.png` at `--eval-cams` poses (from `evalsplat.py cams`);
- `ckpt.pt` and `stats.json` (timings, peak VRAM, Gaussian count).
"""

import argparse
import json
import math
import time
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image


def ssim(a: torch.Tensor, b: torch.Tensor) -> torch.Tensor:
    """Gaussian-window SSIM (11 px, sigma 1.5) of [1,3,H,W] images in [0,1] (fused-ssim is absent)."""
    g = torch.exp(-((torch.arange(11, device=a.device) - 5.0) ** 2) / (2 * 1.5**2))
    g = (g / g.sum())[:, None] * (g / g.sum())[None]
    w = g.expand(3, 1, 11, 11).contiguous()
    f = lambda x: F.conv2d(x, w, padding=5, groups=3)
    mu_a, mu_b = f(a), f(b)
    va, vb, cov = f(a * a) - mu_a**2, f(b * b) - mu_b**2, f(a * b) - mu_a * mu_b
    c1, c2 = 0.01**2, 0.03**2
    s = ((2 * mu_a * mu_b + c1) * (2 * cov + c2)) / ((mu_a**2 + mu_b**2 + c1) * (va + vb + c2))
    return s.mean()


def load_frames(args, dev):
    cam = np.load(args.cameras)
    names = [str(n) for n in cam["names"]]
    K, T_cw, wh = cam["K"].astype(np.float64), cam["T_cw"].astype(np.float64), cam["wh"]
    W0, H0 = int(wh[0][0]), int(wh[0][1])
    s = min(1.0, args.long_edge / max(W0, H0))
    W, H = round(W0 * s), round(H0 * s)  # same rounding as depthfusion_worker (960x540)
    Ks = K.copy()
    Ks[:, :2] *= s
    imgs = []
    for nm in names:
        with Image.open(Path(args.images) / nm) as im:
            imgs.append(np.asarray(im.convert("RGB").resize((W, H), Image.LANCZOS)))
    imgs = torch.from_numpy(np.stack(imgs)).to(dev)  # uint8 [N,H,W,3]

    # SfM init: back-project every observation, one point per 1e-3-extent voxel, colour from the frame
    R, t = T_cw[:, :3, :3], T_cw[:, :3, 3]
    C = -np.einsum("nji,nj->ni", R, t)  # camera centres
    f, uv, z = cam["obs_frame"], cam["obs_uv"], cam["obs_z"]
    rays = np.linalg.solve(K[f], np.c_[uv, np.ones(len(uv))][..., None])[..., 0]
    Xw = np.einsum("nji,nj->ni", R[f], rays * z[:, None] - t[f])
    # normalise the world like gsplat's Parser (centre on the cameras, unit camera extent) so the
    # example's hyper-parameters hold; depth is scaled back to COLMAP units on export
    centre = C.mean(0)
    extent = float(np.linalg.norm(C - centre, axis=1).max())
    wscale = 1.0 / extent
    q = np.round((Xw - centre) * wscale / 1e-3).astype(np.int64)
    _, first = np.unique(q, axis=0, return_index=True)
    px = np.clip((uv[first] * s).astype(int), 0, [W - 1, H - 1])
    rgb = imgs[torch.from_numpy(f[first]), px[:, 1], px[:, 0]].float() / 255
    pts = torch.tensor((Xw[first] - centre) * wscale, dtype=torch.float32, device=dev)

    c2w = np.tile(np.eye(4), (len(names), 1, 1))
    c2w[:, :3, :3] = np.transpose(R, (0, 2, 1))
    c2w[:, :3, 3] = (C - centre) * wscale
    viewmats = torch.linalg.inv(torch.tensor(c2w, dtype=torch.float32, device=dev))
    Ks = torch.tensor(Ks, dtype=torch.float32, device=dev)
    norm = {"centre": centre.tolist(), "wscale": wscale, "extent": extent}
    return names, imgs, viewmats, Ks, (W, H), pts, rgb, norm


def load_priors(pdir: Path, names, viewmats, wscale, dev):
    """Per frame: aligned mono depth (COLMAP units -> normalised world) and camera-frame normals ->
    world. Frames without a prior file get zeros (no prior loss there)."""
    ds, ns = [], []
    for i, nm in enumerate(names):
        p = pdir / f"{nm}.npz"
        if not p.exists():
            ds.append(None)
            ns.append(None)
            continue
        z = np.load(p)
        d = torch.from_numpy(z["d"].astype(np.float32) * wscale).to(dev)
        n = torch.from_numpy(z["n"].astype(np.float32)).to(dev)  # [H,W,3] camera frame
        Rwc = viewmats[i, :3, :3].T
        ds.append(d)
        ns.append(n @ Rwc.T)
    return ds, ns


def rasterize(splats, viewmat, K, W, H, **kw):
    from gsplat import rasterization_2dgs

    return rasterization_2dgs(
        means=splats["means"],
        quats=splats["quats"],
        scales=torch.exp(splats["scales"]),
        opacities=torch.sigmoid(splats["opacities"]),
        colors=splats["sh0"],
        viewmats=viewmat[None],
        Ks=K[None],
        width=W,
        height=H,
        sh_degree=0,
        near_plane=0.01,
        far_plane=100.0,
        **kw,
    )


@torch.no_grad()
def export(splats, out: Path, names, viewmats, Ks, WH, wscale, eval_cams, dev) -> float:
    t0 = time.time()
    W, H = WH
    (out / "depth").mkdir(parents=True, exist_ok=True)
    for i, nm in enumerate(names):
        rc, ra, *_, med, _ = rasterize(splats, viewmats[i], Ks[i], W, H, render_mode="RGB+D")
        d = torch.where(ra[0, ..., 0] > 0.5, med[0, ..., 0], 0) / wscale
        np.savez(out / "depth" / f"{nm}.npz", d=d.cpu().numpy().astype(np.float32))
    if eval_cams is not None:
        (out / "eval").mkdir(exist_ok=True)
        for eid, vm, K in zip(*eval_cams):
            rc, ra, *_ = rasterize(splats, vm, K, W, H, render_mode="RGB")
            img = (rc[0].clamp(0, 1) * 255).byte().cpu().numpy()
            Image.fromarray(img).save(out / "eval" / f"{eid}.png")
            a = (ra[0, ..., 0].clamp(0, 1) * 255).byte().cpu().numpy()
            Image.fromarray(a).save(out / "eval" / f"{eid}_a.png")
    return time.time() - t0


def main(args) -> None:
    from gsplat.strategy import DefaultStrategy

    torch.manual_seed(0)
    dev = "cuda"
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    t_load = time.time()
    names, imgs, viewmats, Ks, (W, H), pts, rgb, norm = load_frames(args, dev)
    wscale = norm["wscale"]
    pri_d = pri_n = None
    if args.priors:
        pri_d, pri_n = load_priors(Path(args.priors), names, viewmats, wscale, dev)
    eval_cams = None
    if args.eval_cams:
        e = np.load(args.eval_cams)
        c2w = np.linalg.inv(e["T_cw"].astype(np.float64))
        c2w[:, :3, 3] = (c2w[:, :3, 3] - np.array(norm["centre"])) * wscale
        vm = torch.linalg.inv(torch.tensor(c2w, dtype=torch.float32, device=dev))
        eval_cams = (
            [str(i) for i in e["ids"]],
            vm,
            torch.tensor(e["K"], dtype=torch.float32, device=dev),
        )
    t_load = time.time() - t_load

    # init (gsplat example): scale = mean distance to 3 nearest neighbours, opacity 0.1
    N = len(pts)
    d2 = torch.cat(
        [torch.cdist(c, pts).topk(4, largest=False).values[:, 1:] for c in pts.split(1024)]
    )
    scales = torch.log(d2.pow(2).mean(-1).sqrt().clamp_min(1e-6))[:, None].repeat(1, 3)
    sh0 = ((rgb - 0.5) / 0.28209479177387814)[:, None]
    splats = torch.nn.ParameterDict({
        "means": torch.nn.Parameter(pts),
        "scales": torch.nn.Parameter(scales),
        "quats": torch.nn.Parameter(torch.rand(N, 4, device=dev)),
        "opacities": torch.nn.Parameter(torch.logit(torch.full((N,), 0.1, device=dev))),
        "sh0": torch.nn.Parameter(sh0),
    })  # fmt: skip
    scene_scale = 1.1  # normalised world
    lrs = {
        "means": 1.6e-4 * scene_scale,
        "scales": 5e-3,
        "quats": 1e-3,
        "opacities": 5e-2,
        "sh0": 2.5e-3,
    }
    opts = {
        k: torch.optim.Adam([{"params": splats[k], "lr": lr}], eps=1e-15) for k, lr in lrs.items()
    }
    sched = torch.optim.lr_scheduler.ExponentialLR(opts["means"], gamma=0.01 ** (1 / args.steps))
    strategy = DefaultStrategy(
        refine_start_iter=500,
        refine_stop_iter=min(15_000, args.steps // 2),
        reset_every=3000,
        refine_every=100,
        key_for_gradient="gradient_2dgs",
    )
    strategy.check_sanity(splats, opts)
    state = strategy.initialize_state(scene_scale=scene_scale)
    # regulariser schedule as a fraction of the run (paper: normal from 7k/30k, distortion 3k/30k)
    normal_start, dist_start = int(args.steps * 7 / 30), int(args.steps * 3 / 30)
    save_at = sorted({s for s in args.save_at if s < args.steps} | {args.steps})

    stats = {
        "args": vars(args),
        "norm": norm,
        "wh": [W, H],
        "init_points": N,
        "load_s": t_load,
        "saves": [],
    }
    torch.cuda.reset_peak_memory_stats()
    t_train, t0, log = 0.0, time.time(), []
    perm = torch.randperm(len(names))
    for step in range(1, args.steps + 1):
        i = int(perm[step % len(names)])
        if step % len(names) == 0:
            perm = torch.randperm(len(names))
        gt = imgs[i].float() / 255
        rc, ra, rn, sn, dist, _med, info = rasterize(
            splats, viewmats[i], Ks[i], W, H, render_mode="RGB+ED", distloss=args.dist_lambda > 0
        )
        color, ed, alpha = rc[0, ..., :3], rc[0, ..., 3], ra[0, ..., 0]
        strategy.step_pre_backward(splats, opts, state, step, info)
        l1 = (color - gt).abs().mean()
        loss = 0.8 * l1 + 0.2 * (1 - ssim(color.permute(2, 0, 1)[None], gt.permute(2, 0, 1)[None]))
        terms = {"l1": l1.item()}
        if args.normal_lambda > 0 and step > normal_start:
            sn = sn.reshape(H, W, 3)  # the port returns [H,W,3] for one camera
            n_err = 1 - (rn[0] * (sn * alpha[..., None].detach())).sum(-1)
            loss = loss + args.normal_lambda * n_err.mean()
            terms["normal"] = n_err.mean().item()
        if args.dist_lambda > 0 and step > dist_start:
            loss = loss + args.dist_lambda * dist.mean()
            terms["dist"] = dist.mean().item()
        if pri_d is not None and pri_d[i] is not None:
            m = (pri_d[i] > 0) & (alpha.detach() > 0.5) & (ed > 0)
            if m.any():
                ld = (ed[m].clamp_min(1e-6).log() - pri_d[i][m].log()).abs().mean()
                nr = F.normalize(rn[0][m], dim=-1)
                ln = (1 - (nr * pri_n[i][m]).sum(-1)).mean()
                loss = loss + args.depth_prior_lambda * ld + args.normal_prior_lambda * ln
                terms.update(prior_depth=ld.item(), prior_normal=ln.item())
        loss.backward()
        strategy.step_post_backward(splats, opts, state, step, info, packed=False)
        for o in opts.values():
            o.step()
            o.zero_grad(set_to_none=True)
        sched.step()
        if not math.isfinite(loss.item()):
            raise RuntimeError(f"non-finite loss at step {step}: {terms}")
        if step % 500 == 0:
            log.append({"step": step, "loss": loss.item(), "n": len(splats["means"]), **terms,
                        "vram_gb": torch.cuda.max_memory_allocated() / 2**30, "t": time.time() - t0})  # fmt: skip
            print(json.dumps(log[-1]), flush=True)
        if step in save_at:
            t_train += time.time() - t0
            sd = out / f"step{step}"
            sd.mkdir(exist_ok=True)
            torch.save({k: v.detach() for k, v in splats.items()} | {"norm": norm}, sd / "ckpt.pt")
            export_s = export(splats, sd, names, viewmats, Ks, (W, H), wscale, eval_cams, dev)
            rec = {"step": step, "train_s": t_train, "export_s": export_s, "n_gauss": len(splats["means"]),
                   "peak_vram_gb": torch.cuda.max_memory_allocated() / 2**30}  # fmt: skip
            stats["saves"].append(rec)
            (sd / "stats.json").write_text(json.dumps(rec | {"norm": norm}, indent=1))
            print(json.dumps(rec), flush=True)
            t0 = time.time()
    stats["log"] = log
    (out / "stats.json").write_text(json.dumps(stats, indent=1))


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--cameras", required=True, help="cameras.npz (pipeline.depthfusion)")
    p.add_argument("--images", required=True, help="undistorted images (COLMAP dense/images)")
    p.add_argument("--out", required=True)
    p.add_argument("--steps", type=int, default=30_000)
    p.add_argument("--save-at", type=int, nargs="*", default=[7_000])
    p.add_argument("--long-edge", type=int, default=960)
    p.add_argument("--normal-lambda", type=float, default=0.05, help="normal consistency; 0 = off")
    p.add_argument("--dist-lambda", type=float, default=0.01, help="depth distortion; 0 = off")
    p.add_argument("--priors", default=None, help="dir of <frame>.npz from priors.py")
    p.add_argument("--depth-prior-lambda", type=float, default=0.1)
    p.add_argument("--normal-prior-lambda", type=float, default=0.05)
    p.add_argument("--eval-cams", default=None, help="eval_cams.npz from evalsplat.py cams")
    return p


if __name__ == "__main__":
    main(build_parser().parse_args())
