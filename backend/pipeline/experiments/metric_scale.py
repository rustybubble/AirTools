"""Sketch: turn a monocular metric-depth model (MoGe-2 / Depth-Anything-V2-Metric) plus an
unitless reconstruction (COLMAP/pycolmap or VGGT) into a single scale factor, metres per
reconstruction-unit.

Companion to `pipeline/calibrate.py`'s `ScaleEstimate(method="metric_depth", ...)` slot -- this
file is a standalone experiment (per `docs/research/p1-gpu-rocm.md` §B), not wired into the
pipeline yet. If it earns its place, `scale_from_metric_depth` below is the function to move into
`calibrate.py` and call from `run.py` alongside `scale_from_gps`/`scale_from_known_distance`.

Method: per frame, sample the *same* set of 2D pixel locations (SfM keypoints, or a grid) in both
(a) the metric-depth model's predicted depth map and (b) the reconstruction's own depth at those
pixels (COLMAP: triangulated point distance to the camera center; VGGT: its own depth-head output,
already same-pixel-aligned with no reprojection needed). Take the robust (median) ratio
metric/recon per frame, then the median of *those* per-frame medians across all frames, with MAD as
the residual -- two-stage robustness because both individual pixels (occlusion, sky, moving
objects) and whole frames (motion blur, a bad depth prediction) can be outliers independently.
"""

from dataclasses import dataclass

import numpy as np


@dataclass
class FrameScaleEstimate:
    frame_id: str
    scale: float  # metres per reconstruction-unit, this frame alone
    n_samples: int  # valid pixel pairs used


def scale_from_depth_pair(
    metric_depth: np.ndarray, recon_depth: np.ndarray, valid: np.ndarray | None = None
) -> tuple[float, int]:
    """Robust median ratio `metric_depth / recon_depth` over valid pixels, one frame.

    Both arrays must already be pixel-aligned (same H,W, same camera) -- for COLMAP that means
    sampling `recon_depth` only at pixels with a triangulated 3D point (everything else is NaN);
    for VGGT, its own dense depth-head output is already dense and aligned by construction. Uses
    the median, not the mean, because reconstructed depth from SfM keypoints is contaminated by
    mismatches/dynamic objects, and metric-depth models are noisiest at depth discontinuities
    (edges, sky) -- a few bad pixels must not move the estimate.
    """
    metric_depth = np.asarray(metric_depth, dtype=np.float64)
    recon_depth = np.asarray(recon_depth, dtype=np.float64)
    ok = np.isfinite(metric_depth) & np.isfinite(recon_depth) & (recon_depth > 1e-6)
    if valid is not None:
        ok &= np.asarray(valid, dtype=bool)
    n = int(ok.sum())
    if n == 0:
        return float("nan"), 0
    ratios = metric_depth[ok] / recon_depth[ok]
    return float(np.median(ratios)), n


def combine_frame_scales(
    per_frame: list[FrameScaleEstimate], min_samples: int = 20
) -> tuple[float, float]:
    """Median scale across frames, with MAD (median absolute deviation) as the residual.

    MAD instead of std: a handful of frames where the depth model or SfM triangulation failed
    outright (whole-frame outliers, e.g. a frame dominated by sky) must not blow up the residual
    the way a couple of extreme values would a standard deviation. Frames with too few valid pixel
    pairs (`min_samples`) are dropped before the frame-level median -- one frame's depth map being
    mostly sky (common for a drone orbit) shouldn't get equal vote with a frame that's mostly
    facade.
    """
    scales = np.array(
        [f.scale for f in per_frame if f.n_samples >= min_samples and np.isfinite(f.scale)]
    )
    if scales.size == 0:
        raise ValueError("combine_frame_scales: no frame had enough valid samples")
    median_scale = float(np.median(scales))
    mad = float(np.median(np.abs(scales - median_scale)))
    return median_scale, mad


def _demo():
    """Self-check: synthetic scene at a known scale, recovered within tolerance despite per-pixel
    noise, per-frame outlier frames, and a completely garbage frame."""
    rng = np.random.default_rng(0)
    true_scale = 23.7  # metres per reconstruction-unit, e.g. VGGT's arbitrary units -> metres
    h, w = 64, 64
    per_frame = []
    for frame_idx in range(10):
        recon_depth = rng.uniform(1.0, 5.0, size=(h, w))  # unitless recon depth
        metric_depth = recon_depth * true_scale
        metric_depth *= 1.0 + rng.normal(0, 0.03, size=(h, w))  # 3% per-pixel sensor noise
        valid = rng.random((h, w)) > 0.1  # 10% invalid (sky/occlusion), like MoGe-2's own mask
        # a chunk of gross mismatches (occluded/dynamic pixels) -- must not move the median
        n_bad = int(0.15 * h * w)
        bad_idx = rng.choice(h * w, n_bad, replace=False)
        metric_depth.flat[bad_idx] *= rng.uniform(0.1, 3.0, size=n_bad)

        scale, n = scale_from_depth_pair(metric_depth, recon_depth, valid)
        per_frame.append(FrameScaleEstimate(frame_id=f"{frame_idx:04d}", scale=scale, n_samples=n))

    # one whole-frame outlier: depth model failed completely on this frame
    per_frame.append(FrameScaleEstimate(frame_id="bad_frame", scale=true_scale * 50, n_samples=500))
    # one frame with too few samples (mostly sky) -- must be dropped by min_samples
    per_frame.append(
        FrameScaleEstimate(frame_id="mostly_sky", scale=true_scale * 0.01, n_samples=3)
    )

    median_scale, mad = combine_frame_scales(per_frame)
    print(f"true_scale={true_scale}  recovered={median_scale:.3f}  mad={mad:.3f}")
    assert abs(median_scale - true_scale) / true_scale < 0.05, "recovered scale off by >5%"
    assert mad / median_scale < 0.10, "MAD residual suspiciously large for 3% pixel noise"
    print("OK")


if __name__ == "__main__":
    _demo()
