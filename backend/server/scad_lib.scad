// E2 frame-safe helper library for LLM-written OpenSCAD (docs/research/assets-llm/e2-code.md).
// Frame (mm): X across (-W/2..W/2, +X right when facing the front), Y up (0..H),
// Z depth (0..D): Z=0 back/mounting face, Z=D front. The harness sets W, H, D, ONLY with -D.
// Everything is axis-aligned boxes and cylinders, so no rotation is ever needed.
// The harness exports one STL per paint colour (ONLY=<hex>), later paints win where they
// overlap, then clips to the W x H x D envelope and maps to the glTF contract (api.md §2).
W = 100; H = 100; D = 100; ONLY = "";
$fn = 40;

// ---- API (the prompt shows this block) ----
// paint(hex) { ... }                      colour group; every solid must be inside one;
//                                          later paints win where they overlap
// box(x0,x1,y0,y1,z0,z1, r=0, ax="z")     box by world ranges (defaults = whole envelope);
//                                          r rounds the 4 edges parallel to axis ax
// cyl(axis, r, a0, a1, c=[0,0], r2)       cylinder/cone along "x"|"y"|"z" from a0 to a1;
//                                          c = centre in the other two axes (x:[y,z] y:[x,z] z:[x,y])
// Face features. face = "front"|"back"|"top"|"bottom"|"left"|"right". c and size use the
// face's two world axes: front/back [x,y], top/bottom [x,z], left/right [z,y].
// Each occupies the outer t mm of the envelope at that face (inside the box).
// panel(face, c, size, t, r=0)            plate (door, lid, screen, flange); in difference() = pocket
// disc(face, c, dia, t)                   round plate/knob/boss; in difference() = round pocket
// hole(face, c, dia, t=big)               through-hole along the face normal (use in difference())
// grille(face, c, size, n, dir="h", t=4)  n parallel bars ("h" horizontal, "v" vertical)
// vent(face, c, size, n, dir="h", t=4)    n slots, use in difference() to cut louvres
// handle(face, c, len, dir="v", t=40, dia=20)  bar handle on two posts, t = standoff depth
// To make a feature stand proud, stop the body short of that face: box(z1=D-30) then panel(t=30).

module paint(c) { echo(str("E2COLOR=", c)); if (ONLY == "" || ONLY == c) children(); }

module box(x0=-W/2, x1=W/2, y0=0, y1=H, z0=0, z1=D, r=0, ax="z") {
    lo = [min(x0, x1), min(y0, y1), min(z0, z1)];
    s = [abs(x1 - x0), abs(y1 - y0), abs(z1 - z0)];
    if (r <= 0) translate(lo) cube(s);
    else {
        // hull of 4 cylinders along ax, inset by r
        i = ax == "x" ? 0 : ax == "y" ? 1 : 2;
        a = i == 0 ? 1 : 0; b = i == 2 ? 1 : 2;
        rr = min(r, s[a] / 2 - 0.01, s[b] / 2 - 0.01);
        hull() for (u = [lo[a] + rr, lo[a] + s[a] - rr], v = [lo[b] + rr, lo[b] + s[b] - rr])
            cyl(ax, rr, lo[i], lo[i] + s[i], [u, v]);
    }
}

module cyl(axis, r, a0, a1, c=[0, 0], r2=undef) {
    s = min(a0, a1); h = abs(a1 - a0); rt = is_undef(r2) ? r : r2;
    rb = a1 >= a0 ? r : rt; re = a1 >= a0 ? rt : r;
    if (axis == "x") translate([s, c[0], c[1]]) rotate([0, 90, 0]) cylinder(h=h, r1=rb, r2=re);
    else if (axis == "y") translate([c[0], s, c[1]]) rotate([-90, 0, 0]) cylinder(h=h, r1=rb, r2=re);
    else translate([c[0], c[1], s]) cylinder(h=h, r1=rb, r2=re);
}

// face -> [normal axis, in-plane axis a, in-plane axis b, face coordinate, inward sign]
function _f(face) =
    face == "front" ? [2, 0, 1, D, -1] : face == "back" ? [2, 0, 1, 0, 1] :
    face == "top" ? [1, 0, 2, H, -1] : face == "bottom" ? [1, 0, 2, 0, 1] :
    face == "right" ? [0, 2, 1, W/2, -1] : face == "left" ? [0, 2, 1, -W/2, 1] :
    assert(false, str("unknown face ", face));

// box on a face: in-plane ranges from c/size, normal range = outer t mm (+ out mm outside)
module _fbox(face, c, size, t, out=0) {
    f = _f(face);
    lo = [0, 0, 0]; n0 = f[3] - f[4] * out; n1 = f[3] + f[4] * t;
    p = [for (k = [0:2]) k == f[0] ? min(n0, n1) : k == f[1] ? c[0] - size[0] / 2 : c[1] - size[1] / 2];
    s = [for (k = [0:2]) k == f[0] ? abs(n1 - n0) : k == f[1] ? size[0] : size[1]];
    translate(p) cube(s);
}

module _fcyl(face, c, dia, t, out=0) {
    f = _f(face); n0 = f[3] - f[4] * out; n1 = f[3] + f[4] * t;
    ax = f[0] == 0 ? "x" : f[0] == 1 ? "y" : "z";
    cc = f[0] == 1 ? [c[0], c[1]] : f[0] == 2 ? [c[0], c[1]] : [c[1], c[0]];
    cyl(ax, dia / 2, n0, n1, cc);
}

module panel(face, c=[0, 0], size=[10, 10], t=5, r=0) {
    if (r <= 0) _fbox(face, c, size, t);
    else {
        f = _f(face); r2 = min(r, size[0] / 2 - 0.01, size[1] / 2 - 0.01);
        hull() for (du = [-1, 1], dv = [-1, 1])
            _fcyl(face, [c[0] + du * (size[0] / 2 - r2), c[1] + dv * (size[1] / 2 - r2)], 2 * r2, t);
    }
}

module disc(face, c=[0, 0], dia=10, t=5) _fcyl(face, c, dia, t);

module hole(face, c=[0, 0], dia=5, t=1e5) _fcyl(face, c, dia, t, out=1);

module grille(face, c=[0, 0], size=[100, 100], n=8, dir="h", t=4) {
    k = dir == "h" ? 1 : 0;  // bars stack along b (horizontal bars) or a (vertical bars)
    pitch = size[k] / n; bar = pitch / 2;
    for (j = [0:n - 1]) {
        off = -size[k] / 2 + pitch * (j + 0.5);
        cc = k == 1 ? [c[0], c[1] + off] : [c[0] + off, c[1]];
        ss = k == 1 ? [size[0], bar] : [bar, size[1]];
        _fbox(face, cc, ss, t);
    }
}

module vent(face, c=[0, 0], size=[100, 100], n=8, dir="h", t=4) {
    k = dir == "h" ? 1 : 0;
    pitch = size[k] / n; slot = pitch / 2;
    for (j = [0:n - 1]) {
        off = -size[k] / 2 + pitch * (j + 0.5);
        cc = k == 1 ? [c[0], c[1] + off] : [c[0] + off, c[1]];
        ss = k == 1 ? [size[0], slot] : [slot, size[1]];
        _fbox(face, cc, ss, t, out=1);
    }
}

module handle(face, c=[0, 0], len=200, dir="v", t=40, dia=20) {
    f = _f(face); k = dir == "v" ? 1 : 0;  // "v": along the face's second axis
    // bar: outer dia mm of the standoff; posts: from the body surface (depth t) to the bar
    for (s = [-1, 1]) {
        pc = k == 1 ? [c[0], c[1] + s * (len / 2 - dia / 2)] : [c[0] + s * (len / 2 - dia / 2), c[1]];
        _fcyl(face, pc, dia * 0.8, t);
    }
    bs = k == 1 ? [dia, len] : [len, dia];
    _fbox(face, c, bs, dia);
}
