import argparse
import json

from pipeline.experiments.structure.score import score, summary_line


def main(argv=None) -> None:
    p = argparse.ArgumentParser(prog="python -m pipeline.experiments.structure")
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("score", help="score a scene (+ optional structure layer) against gt.json")
    s.add_argument("scene", help="scene dir (scene.json + mesh/collision/cameras)")
    s.add_argument("--gt", required=True)
    s.add_argument("--structure", default=None, help="structure.json (schema airtools.structure/1)")
    s.add_argument("--mesh", default=None, help="score this glb instead of the scene's mesh")
    s.add_argument("--frames", default=None, help="the run's frames/ dir, for LSD reprojection")
    s.add_argument("--out", required=True, help="results.jsonl (one line appended)")
    s.add_argument("--name", required=True)
    s.add_argument("--radius", type=float, default=0.03, help="snap radius (m, GT scale)")
    s.add_argument("--margin", type=float, default=0.015, help="planarity: inset from region edge")
    s.add_argument("--band", type=float, default=0.02, help="planarity: max |dist| to GT plane")
    s.add_argument("--aim", type=float, default=0.01, help="aim model: tip within this of GT (m)")
    s.add_argument(
        "--grades", default="AB", help="GT point grades scored (C = suspect, excluded by default)"
    )
    s.add_argument("--aim-n", type=int, default=16, help="aim samples per GT point")
    s.add_argument("--id-fps", type=float, default=10.0)
    s.add_argument("--note", default="")
    s.add_argument("--json", action="store_true", help="print the full result")
    a = p.parse_args(argv)
    r = score(a)
    if a.json:
        print(json.dumps({k: v for k, v in r.items() if k != "per_point"}, indent=1, default=float))
    print(summary_line(r))


main()
