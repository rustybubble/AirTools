"""SAM 2.1 box -> mask worker for pipeline/parts.py. Runs under `AIRTOOLS_SAM_PYTHON`, a python with
torch + transformers (not the project env: torch is too big for it). Reads a job JSON
`{"model": ..., "items": [{"image": path, "box": [x0, y0, x1, y1], "out": path}]}` and writes one
binary PNG mask per item. Prints `{"n": ..., "load_s": ..., "per_image_s": ...}` on stdout.
"""

import json
import sys
import time

import numpy as np
import torch
from PIL import Image
from transformers import Sam2Model, Sam2Processor


def main(job_path: str) -> None:
    with open(job_path) as fh:
        job = json.load(fh)
    t0 = time.time()
    proc = Sam2Processor.from_pretrained(job["model"])
    model = Sam2Model.from_pretrained(job["model"]).eval()
    load_s, t1 = time.time() - t0, time.time()
    for it in job["items"]:
        im = Image.open(it["image"]).convert("RGB")
        inp = proc(images=im, input_boxes=[[[float(x) for x in it["box"]]]], return_tensors="pt")
        with torch.no_grad():
            out = model(**inp, multimask_output=False)
        m = proc.post_process_masks(out.pred_masks, inp["original_sizes"])[0][0, 0].numpy() > 0
        Image.fromarray((m * 255).astype(np.uint8)).save(it["out"])
    n = len(job["items"])
    print(json.dumps({"n": n, "load_s": load_s, "per_image_s": (time.time() - t1) / max(n, 1)}))


if __name__ == "__main__":
    main(sys.argv[1])
