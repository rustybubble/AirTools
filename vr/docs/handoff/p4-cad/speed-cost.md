# grok_scad_run per writer: every run (cad lane, 2026-09-27)

`server/experiments/scad_bench.py`, the three parts at once per writer, a fresh LLM cache (every call live),
OpenSCAD 2026.09.23 (manifold backend) on the Mac. Raw bbox = Grok's model vs the W×H×D envelope before the
harness snaps it (first answer → final); every served CAD model is exactly the listed size. Seconds = the whole loop.

| Writer | Part | Built | LLM calls | Fixes | Fit pass | Seconds | $ | Prompt / completion (reasoning) tokens | Raw bbox % |
|---|---|---|---|---|---|---|---|---|---|
| xai:grok-4.7 · default effort | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 1 | 0 | - | 216.0 | 0.0989 | 2702 / 540 (15713) | 0.0 → 0.0 |
| xai:grok-4.7 · default effort | window (JELD-WEN V-2500, 1511×1207×83 mm) | yes | 1 | 0 | - | 400.4 | 0.2039 | 2724 / 1163 (32196) | 0.0 → 0.0 |
| xai:grok-4.7 · default effort | knob (Liberty Charmaine, 28×28×28 mm) | yes | 1 | 0 | - | 228.7 | 0.1068 | 2699 / 472 (16716) | 0.0 → 0.0 |
| xai:grok-4.7 · medium | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 1 | 0 | - | 202.2 | 0.0940 | 2702 / 715 (14348) | 0.0 → 0.0 |
| xai:grok-4.7 · medium | window (JELD-WEN V-2500, 1511×1207×83 mm) | yes | 1 | 0 | - | 331.7 | 0.1651 | 2724 / 960 (25937) | 0.0 → 0.0 |
| xai:grok-4.7 · medium | knob (Liberty Charmaine, 28×28×28 mm) | yes | 1 | 0 | - | 178.4 | 0.0839 | 2699 / 614 (12755) | 0.0 → 0.0 |
| xai:grok-4.7 · low (round 1) | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 1 | 0 | - | 33.5 | 0.0164 | 2702 / 469 (1653) | 0.0 → 0.0 |
| xai:grok-4.7 · low (round 1) | window (JELD-WEN V-2500, 1511×1207×83 mm) | yes | 1 | 0 | - | 79.7 | 0.0402 | 2724 / 838 (5245) | 0.0 → 0.0 |
| xai:grok-4.7 · low (round 1) | knob (Liberty Charmaine, 28×28×28 mm) | yes | 1 | 0 | - | 64.4 | 0.0336 | 2699 / 528 (4452) | 0.07 → 0.07 |
| xai:grok-4.7 · low (round 2) | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 1 | 0 | - | 66.5 | 0.0295 | 2702 / 464 (4226) | 0.0 → 0.0 |
| xai:grok-4.7 · low (round 2) | window (JELD-WEN V-2500, 1511×1207×83 mm) | yes | 1 | 0 | - | 126.7 | 0.0561 | 2724 / 1114 (7996) | 0.0 → 0.0 |
| xai:grok-4.7 · low (round 2) | knob (Liberty Charmaine, 28×28×28 mm) | yes | 1 | 0 | - | 91.1 | 0.0441 | 2699 / 887 (5845) | 0.0 → 0.0 |
| xai:grok-4.20-0309-reasoning | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 1 | 0 | - | 117.8 | 0.0289 | 1607 / 356 (10440) | 0.0 → 0.0 |
| xai:grok-4.20-0309-reasoning | window (JELD-WEN V-2500, 1511×1207×83 mm) | yes | 1 | 0 | - | 126.7 | 0.0313 | 1629 / 600 (11183) | 1.21 → 1.21 |
| xai:grok-4.20-0309-reasoning | knob (Liberty Charmaine, 28×28×28 mm) | yes | 1 | 0 | - | 179.6 | 0.0470 | 1605 / 543 (17519) | 0.0 → 0.0 |
| xai:grok-4.3 · low | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 1 | 0 | - | 13.8 | 0.0042 | 1615 / 288 (675) | 0.0 → 0.0 |
| xai:grok-4.3 · low | window (JELD-WEN V-2500, 1511×1207×83 mm) | yes | 1 | 0 | - | 17.4 | 0.0055 | 1637 / 380 (1082) | 0.0 → 0.0 |
| xai:grok-4.3 · low | knob (Liberty Charmaine, 28×28×28 mm) | yes | 2 | 1 | - | 26.6 | 0.0098 | 3085 / 605 (1955) | 0.0 → 0.0 |
| xai:grok-4.20-0309-non-reasoning · 1 answer, 2 fixes | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 2 | 0 | yes | 12.1 | 0.0049 | 3367 / 1342 (0) | 48.74 → 48.43 |
| xai:grok-4.20-0309-non-reasoning · 1 answer, 2 fixes | window (JELD-WEN V-2500, 1511×1207×83 mm) | yes | 2 | 0 | yes | 18.9 | 0.0092 | 4023 / 2405 (0) | 2.42 → 2.42 |
| xai:grok-4.20-0309-non-reasoning · 1 answer, 2 fixes | knob (Liberty Charmaine, 28×28×28 mm) | yes | 3 | 1 | yes | 11.9 | 0.0054 | 4677 / 1316 (0) | 2.0 → 2.0 |
| xai:grok-4.20-0309-non-reasoning · 3 answers (first that builds), 2 fixes | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 3 | 0 | - | 8.6 | 0.0074 | 4815 / 1967 (0) | 0.0 → 0.0 |
| xai:grok-4.20-0309-non-reasoning · 3 answers (first that builds), 2 fixes | window (JELD-WEN V-2500, 1511×1207×83 mm) | yes | 4 | 0 | yes | 15.0 | 0.0134 | 6648 / 3278 (0) | 50.0 → 50.0 |
| xai:grok-4.20-0309-non-reasoning · 3 answers (first that builds), 2 fixes | knob (Liberty Charmaine, 28×28×28 mm) | yes | 4 | 0 | yes | 11.9 | 0.0088 | 6629 / 2287 (0) | 44.5 → 44.5 |
| xai:grok-4.20-0309-non-reasoning · 3 answers (lowest bbox wins), 2 fixes | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 4 | 0 | yes | 11.0 | 0.0087 | 6522 / 2288 (0) | 26.62 → 26.62 |
| xai:grok-4.20-0309-non-reasoning · 3 answers (lowest bbox wins), 2 fixes | window (JELD-WEN V-2500, 1511×1207×83 mm) | yes | 4 | 0 | yes | 18.2 | 0.0149 | 7235 / 4396 (0) | 2.32 → 2.32 |
| xai:grok-4.20-0309-non-reasoning · 3 answers (lowest bbox wins), 2 fixes | knob (Liberty Charmaine, 28×28×28 mm) | yes | 5 | 2 | - | 11.3 | 0.0095 | 7898 / 2312 (0) | 0.0 → 0.0 |
| xai:grok-4.20-0309-non-reasoning · 1 answer, 1 fix, old error text | dishwasher (Frigidaire FDPC4221AS, 610×889×635 mm) | yes | 2 | 1 | - | 11.4 | 0.0067 | 3249 / 1183 (0) | 0.16 → 0.16 |
| xai:grok-4.20-0309-non-reasoning · 1 answer, 1 fix, old error text | window (JELD-WEN V-2500, 1511×1207×83 mm) | no (ERROR: Assertion 'false' failed: "unknown face undef" in model.scad, l) | 2 | 1 | - | 12.1 | 0.0077 | 3443 / 1476 (0) | — |
| xai:grok-4.20-0309-non-reasoning · 1 answer, 1 fix, old error text | knob (Liberty Charmaine, 28×28×28 mm) | no (Mesh assembly failed: the model fills only part of the envelope (bbox ) | 2 | 1 | - | 9.5 | 0.0061 | 3129 / 1025 (0) | — |
