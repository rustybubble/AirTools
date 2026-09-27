"""CLI: `probe`/`frames`/`telemetry` (tool-independent capture step) plus `run` (assignment §7,
full video -> scene package pipeline: pycolmap SfM -> OpenMVS dense/mesh/texture -> calibration ->
package)."""

import argparse
import json
import logging
import shlex
import sys
from dataclasses import replace
from pathlib import Path

from pipeline import frames, qa, telemetry
from pipeline.package import write_json_atomic


def _cmd_probe(args: argparse.Namespace) -> None:
    print(json.dumps(frames.probe(args.video), indent=2))


def _cmd_frames(args: argparse.Namespace) -> None:
    paths = frames.extract_frames(args.video, args.out_dir, fps=args.fps, long_edge=args.long_edge)
    print(f"wrote {len(paths)} frames to {args.out_dir}")


def _cmd_telemetry(args: argparse.Namespace) -> None:
    srt_path = frames.extract_srt(args.video, args.out)
    if srt_path is None:
        print("no subtitle/telemetry stream found")
        return
    records = telemetry.parse_srt(srt_path.read_text())
    print(f"parsed {len(records)} telemetry records -> {srt_path}")


def _cmd_run(args: argparse.Namespace) -> None:
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    from pipeline import run as run_module  # lazy: needs the `pipeline` extra (pycolmap/pymeshlab)

    work_dir = args.work if args.work is not None else Path("work") / args.site
    report = run_module.run(
        args.video,
        args.site,
        args.out,
        work_dir,
        stages=args.stages,
        preview_frames=args.preview_frames,
        start_s=args.start,
        end_s=args.end,
        fps=args.fps,
        long_edge=args.long_edge,
        sharpen_window=args.sharpen_window,
        srt_path=args.srt,
        known_height_m=args.known_height_m,
        known_distance_json=args.known_distance_json,
        board=args.board,
        board_marker_m=args.board_marker_m,
        board_gap_m=args.board_gap_m,
        board_cols=args.board_cols,
        board_rows=args.board_rows,
        board_distance_m=args.board_distance_m,
        mapper=args.mapper,
        resolution_level=args.resolution_level,
        number_views=args.number_views,
        densify=args.densify,
        densify_args=tuple(shlex.split(args.densify_args)),
        mesh_args=tuple(shlex.split(args.mesh_args)),
        texture_args=tuple(shlex.split(args.texture_args)),
        max_texture_size=args.max_texture_size,
        texture_decimate=args.texture_decimate,
        texture_resolution_level=args.texture_resolution_level,
        target_triangles=args.target_triangles,
        collision_triangles=args.collision_triangles,
        crop=args.crop,
        holdout_every=args.holdout_every,
        force=args.force,
        threads=args.threads,
        structure=args.structure == "on",
    )
    print(json.dumps(report, indent=2))


def _cmd_board(args: argparse.Namespace) -> None:
    from pipeline import aruco  # lazy: needs the `pipeline` extra (opencv-python-headless)

    if args.kind == "board":
        spec = aruco.BoardSpec()
        if args.size_m is not None:
            factor = args.size_m / spec.width_m
            spec = replace(
                spec, marker_length_m=spec.marker_length_m * factor, gap_m=spec.gap_m * factor
            )
    else:
        spec = aruco.ScaleBarSpec()
        if args.size_m is not None:
            spec = replace(spec, center_distance_m=args.size_m)

    image = aruco.make_board_image(spec, dpi=args.dpi)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    if args.out.suffix.lower() == ".pdf":
        image.save(args.out, resolution=args.dpi)
    else:
        image.save(args.out)
    print(f"wrote {args.out} ({image.width}x{image.height}px @ {args.dpi} dpi)")

    if args.tile != "none":
        tiles = aruco.tile_for_printing(image, args.dpi, page=args.tile)
        tiled_path = args.out.with_name(f"{args.out.stem}_tiled_{args.tile}.pdf")
        tiles[0].save(tiled_path, save_all=True, append_images=tiles[1:], resolution=args.dpi)
        print(f"wrote {tiled_path} ({len(tiles)} {args.tile} pages)")


def _cmd_qa(args: argparse.Namespace) -> None:
    out_dir = args.out if args.out is not None else args.scene_dir
    ids = args.ids or qa.default_ids(
        args.scene_dir, args.frames_dir, holdout_every=args.holdout_every
    )
    result = qa.photo_consistency(args.scene_dir, args.frames_dir, ids, out_dir=out_dir)
    write_json_atomic(Path(out_dir) / "qa.json", json.dumps(result, indent=2))
    print(json.dumps(result, indent=2))


def _cmd_remote_run(args: argparse.Namespace) -> None:
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    from pipeline import remote  # lazy: mirrors _cmd_run's lazy import of pipeline.run

    exit_code = remote.remote_run(
        args.video,
        args.site,
        args.out,
        host=args.host,
        remote_root=args.remote_root,
        upload=args.upload,
        extra_args=args.extra_args,
    )
    raise SystemExit(exit_code)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="pipeline")
    sub = parser.add_subparsers(dest="command", required=True)

    p_probe = sub.add_parser(
        "probe", help="ffprobe a video: duration, resolution, fps, subtitle track"
    )
    p_probe.add_argument("video", type=Path)
    p_probe.set_defaults(func=_cmd_probe)

    p_frames = sub.add_parser("frames", help="extract frames from a video")
    p_frames.add_argument("video", type=Path)
    p_frames.add_argument("out_dir", type=Path)
    p_frames.add_argument("--fps", type=float, default=3.0)
    p_frames.add_argument("--long-edge", type=int, default=1920)
    p_frames.set_defaults(func=_cmd_frames)

    p_telemetry = sub.add_parser("telemetry", help="extract + parse DJI SRT telemetry from a video")
    p_telemetry.add_argument("video", type=Path)
    p_telemetry.add_argument("out", type=Path, help="output telemetry.srt path")
    p_telemetry.set_defaults(func=_cmd_telemetry)

    p_run = sub.add_parser(
        "run", help="end to end: video -> scene package (SfM + OpenMVS + calibration + packaging)"
    )
    p_run.add_argument("video", type=Path)
    p_run.add_argument("--site", required=True, help="site name, e.g. 'strasbourg-cathedral-spire'")
    p_run.add_argument("--out", type=Path, required=True, help="scene package output dir")
    p_run.add_argument("--work", type=Path, default=None, help="work dir (default work/<site>)")
    p_run.add_argument(
        "--stages",
        default="preview,full",
        help="comma-separated subset of 'preview,full' (plan §1a): 'preview' (Stage A, <1 min "
        "target, publishes revision 1) then 'full' (Stage B, the existing chain, rigid-aligns "
        "onto the preview if one is published and publishes revision 2) run by default; "
        "'--stages preview' or '--stages full' runs just one",
    )
    p_run.add_argument(
        "--preview-frames",
        type=int,
        default=48,
        dest="preview_frames",
        help="Stage A frame count, evenly spaced, <=64 (plan §2.2)",
    )
    p_run.add_argument("--start", type=float, default=None, dest="start")
    p_run.add_argument("--end", type=float, default=None, dest="end")
    p_run.add_argument("--fps", type=float, default=2.0)
    p_run.add_argument("--long-edge", type=int, default=1920)
    p_run.add_argument(
        "--sharpen-window", type=int, default=1, help="keep sharpest of every N frames"
    )
    p_run.add_argument(
        "--srt", type=Path, default=None, help="override auto-extracted telemetry SRT"
    )
    p_run.add_argument("--known-height-m", type=float, default=None, dest="known_height_m")
    p_run.add_argument(
        "--known-distance-json",
        type=Path,
        default=None,
        dest="known_distance_json",
        help="metric scale from a hand-measured distance between two points clicked in >=2 "
        'frames: {"length_m": 1.52, "observations": [{"frame": "0012.jpg", "a": [x, y], "b": '
        '[x, y]}, {"frame": "0045.jpg", "a": [x, y], "b": [x, y]}]} -- "frame" matches a '
        "registered frame's basename (the frames SfM ran on, e.g. work/<site>/frames/0012.jpg, "
        "not a full-resolution re-extraction), a/b are the same two physical points in pixel "
        "coordinates, length_m is their real hand-measured distance. See "
        "pipeline.calibrate.known_distance_scale.",
    )
    p_run.add_argument(
        "--board",
        choices=["auto", "none", "board", "scalebar"],
        default="auto",
        help="detect a printed ArUco board/scale-bar (pipeline.cli board) on full-resolution "
        "re-extracted frames and use it for scale if found (pipeline.aruco): 'auto' (default) "
        "tries BoardSpec then ScaleBarSpec, silently skipping either that isn't detected; "
        "'board'/'scalebar' force one; 'none' skips detection entirely (no full-res "
        "re-extraction either, saving the extra ffmpeg pass)",
    )
    p_run.add_argument(
        "--board-marker-m",
        type=float,
        default=None,
        dest="board_marker_m",
        help="printed marker side length in m (default: aruco.BoardSpec/ScaleBarSpec's own "
        "default -- must match what was actually printed, see pipeline.cli board)",
    )
    p_run.add_argument(
        "--board-gap-m",
        type=float,
        default=None,
        dest="board_gap_m",
        help="BoardSpec marker gap in m",
    )
    p_run.add_argument(
        "--board-cols", type=int, default=None, dest="board_cols", help="BoardSpec grid columns"
    )
    p_run.add_argument(
        "--board-rows", type=int, default=None, dest="board_rows", help="BoardSpec grid rows"
    )
    p_run.add_argument(
        "--board-distance-m",
        type=float,
        default=None,
        dest="board_distance_m",
        help="ScaleBarSpec marker centre-to-centre distance in m",
    )
    p_run.add_argument(
        "--mapper",
        choices=["global", "incremental", "vggt"],
        default="global",
        help="'vggt' forces the VGGT ROCm-GPU fallback pose estimator (pipeline.vggt) instead of "
        "pycolmap -- for testing the fallback on demand; it also runs automatically when "
        "pycolmap registers <60%% of frames or raises, if AIRTOOLS_VGGT_ENV is set",
    )
    p_run.add_argument(
        "--resolution-level",
        type=int,
        default=1,
        help="OpenMVS DensifyPointCloud budget lever (0 = full-res depth maps, ~3x slower; 1 = "
        "half-res, the default: bench E1 found it fills more of the room at equal texture quality; "
        "2 needs --densify-args '--min-resolution 320' to differ from 1 on 1080p frames)",
    )
    p_run.add_argument(
        "--number-views",
        type=int,
        default=3,
        dest="number_views",
        help="DensifyPointCloud --number-views (views per depth map; 0 = all neighbours)",
    )
    p_run.add_argument(
        "--densify",
        choices=["openmvs", "depthfusion", "hybrid"],
        default="hybrid",
        help="dense geometry: 'hybrid', the default (a res-1 OpenMVS depth pass kept where it "
        "agrees with MoGe-2 mono depth, which fills the rest; needs AIRTOOLS_DEPTH_ENV and falls "
        "back to 'openmvs' with a warning without it or if depth inference fails; report.json "
        "'densify' says which ran), 'openmvs' (DensifyPointCloud+ReconstructMesh), or "
        "'depthfusion' (mono depth aligned to the SfM points + TSDF; pipeline/depthfusion.py)",
    )
    p_run.add_argument(
        "--densify-args",
        default="",
        dest="densify_args",
        help="extra raw DensifyPointCloud flags, e.g. '--sub-resolution-levels 1 --iters 2' "
        "(with --densify depthfusion/hybrid: depthfusion_worker.py flags, e.g. '--align affine')",
    )
    p_run.add_argument(
        "--mesh-args",
        default="",
        dest="mesh_args",
        help="extra raw ReconstructMesh flags, e.g. '--free-space-support 1'",
    )
    p_run.add_argument(
        "--texture-args",
        default="",
        dest="texture_args",
        help="extra raw TextureMesh flags, e.g. '--outlier-threshold 0'",
    )
    p_run.add_argument("--max-texture-size", type=int, default=4096, dest="max_texture_size")
    p_run.add_argument(
        "--texture-decimate",
        type=float,
        default=None,
        dest="texture_decimate",
        help="TextureMesh --decimate override, 0..1 (default: auto from --target-triangles)",
    )
    p_run.add_argument(
        "--texture-resolution-level",
        type=int,
        default=0,
        dest="texture_resolution_level",
        help="TextureMesh --resolution-level (0 = full-res photos for texturing)",
    )
    p_run.add_argument("--target-triangles", type=int, default=200_000, dest="target_triangles")
    p_run.add_argument(
        "--collision-triangles", type=int, default=50_000, dest="collision_triangles"
    )
    p_run.add_argument(
        "--crop",
        choices=["auto", "none", "orbit"],
        default="auto",
        help="auto-crop the dense mesh to the subject before texturing (mesh.py's crop_to_subject_"
        "auto/_orbit, run between OpenMVS's ReconstructMesh and TextureMesh): 'auto' (default) "
        "uses view-centrality, works for orbits and flybys alike; 'orbit' is the legacy single-"
        "target-point crop (converging orbits only, skips itself on a flyby); 'none' disables "
        "cropping",
    )
    p_run.add_argument(
        "--holdout-every",
        type=int,
        default=5,
        dest="holdout_every",
        help="extract frames --holdout-every times denser than --fps and give SfM only every "
        "Nth one, so the rest are genuine held-out frames for the auto-wired QA pass to measure "
        "generalisation on (0 disables: extract at --fps directly, no held-out frames)",
    )
    p_run.add_argument(
        "--force", action="store_true", help="re-run every stage, ignore resume markers"
    )
    p_run.add_argument(
        "--threads",
        type=int,
        default=None,
        help="cap SfM/OpenMVS threads (default: 4 for SfM, os.cpu_count() for OpenMVS) -- lower on "
        "a memory-constrained box, each thread holds its own image/depth-map working set",
    )
    p_run.add_argument(
        "--structure",
        choices=["on", "off"],
        default="on",
        help="full stage: write structure.r<rev>.json (LIMAP lines + PxwPlanar planes + S4 rects, "
        "pipeline/structure.py) beside the mesh; needs AIRTOOLS_LIMAP_ENV and/or "
        "AIRTOOLS_PXW_ENV, else skipped with a warning; never fails the mesh publish",
    )
    p_run.set_defaults(func=_cmd_run)

    p_board = sub.add_parser(
        "board", help="print-ready ArUco board/scale-bar PDF+PNG for the capture team (plan §2.1)"
    )
    p_board.add_argument("--out", type=Path, required=True, help="output path, .pdf or .png")
    p_board.add_argument("--kind", choices=["board", "scalebar"], default="board")
    p_board.add_argument(
        "--size-m",
        type=float,
        default=None,
        dest="size_m",
        help="board: overall board width in m (scales marker/gap size); scalebar: end-to-end "
        "marker centre distance in m (default: each spec's own default size)",
    )
    p_board.add_argument("--dpi", type=int, default=300)
    p_board.add_argument(
        "--tile",
        choices=["letter", "a4", "none"],
        default="letter",
        help="also write a tiled multi-page PDF sized for a home/office printer",
    )
    p_board.set_defaults(func=_cmd_board)

    p_qa = sub.add_parser(
        "qa", help="render QA: compare mesh.glb renders against real photos (PSNR/SSIM/coverage)"
    )
    p_qa.add_argument("scene_dir", type=Path)
    p_qa.add_argument(
        "--frames", type=Path, required=True, dest="frames_dir", help="directory of real photos"
    )
    p_qa.add_argument(
        "--ids",
        nargs="*",
        default=None,
        help="frame ids to check (default: all registered + held-out frames, see --holdout-every)",
    )
    p_qa.add_argument(
        "--holdout-every",
        type=int,
        default=5,
        dest="holdout_every",
        help="also score every Nth frame in --frames not in cameras.json (0 disables)",
    )
    p_qa.add_argument(
        "--out", type=Path, default=None, help="output dir for qa.json + PNGs (default: scene_dir)"
    )
    p_qa.set_defaults(func=_cmd_qa)

    p_remote = sub.add_parser(
        "remote-run",
        help="upload a video to a GPU box (default hfbox), run `pipeline.cli run` there in a "
        "tmux session, pull each published revision into --out as it lands (plan §1a)",
    )
    p_remote.add_argument("video", type=Path)
    p_remote.add_argument("--site", required=True, help="site name, e.g. 'klaus-east-window'")
    p_remote.add_argument("--host", default="hfbox")
    p_remote.add_argument(
        "--remote-root",
        default="/workspace/airtools",
        dest="remote_root",
        help="repo root on the remote box",
    )
    p_remote.add_argument("--out", type=Path, required=True, help="local scene package output dir")
    p_remote.add_argument(
        "--upload",
        choices=["video", "frames"],
        default="video",
        help="'video' (default): rsync the raw video (resumable, --partial), the remote run "
        "extracts frames itself. 'frames' is not implemented yet -- pipeline.run.run() has no "
        "frames-dir entry point to hand off to.",
    )
    p_remote.set_defaults(func=_cmd_remote_run)

    return parser


def main(argv: list[str] | None = None) -> None:
    # `remote-run ... -- <extra args forwarded to the remote `run`>`: split off a literal `--`
    # ourselves before argparse sees it (argparse.REMAINDER on a positional after `video` swallows
    # every later flag, including --site/--out, not just the tail after `--`).
    argv = list(sys.argv[1:] if argv is None else argv)
    extra_args: list[str] = []
    if "--" in argv:
        idx = argv.index("--")
        extra_args = argv[idx + 1 :]
        argv = argv[:idx]
    args = build_parser().parse_args(argv)
    args.extra_args = extra_args
    args.func(args)


if __name__ == "__main__":
    main()
