#!/usr/bin/env python3
"""
Minimal but real recursive ray tracer (numpy) for the Thailand Trust List
Futuristic-Neon UI. Renders glossy spheres on a reflective floor lit by
neon emissive area-ish lights, with shadows, reflections and a Fresnel
term. Output is genuine ray-traced PNG imagery (not a CSS gradient).

Two scenes:
  hero  -> wide banner (login / directory header)
  badge -> square emblem (brand / favicon-ish)
"""
import sys, math
import numpy as np
from PIL import Image

def norm(v):
    return v / np.linalg.norm(v, axis=-1, keepdims=True)

def normalize(v):
    n = math.sqrt(sum(c*c for c in v))
    return np.array([c/n for c in v], dtype=np.float64)

# ---- scene primitives (plain dicts for simple vectorization) ----------
def make_sphere(c, r, color, reflect=0.4, spec=0.5, emissive=None):
    return dict(kind='sphere', c=np.array(c, float), r=float(r),
                color=np.array(color, float), reflect=reflect, spec=spec,
                emissive=None if emissive is None else np.array(emissive, float))

def make_plane(p, n, c1, c2, reflect=0.35):
    return dict(kind='plane', p=np.array(p, float), n=normalize(n),
                c1=np.array(c1, float), c2=np.array(c2, float), reflect=reflect)

# ---- intersection (vectorized over a ray batch) -----------------------
def intersect(obj, O, D):
    if obj['kind'] == 'sphere':
        OC = O - obj['c']
        b = np.einsum('ij,ij->i', OC, D)
        c = np.einsum('ij,ij->i', OC, OC) - obj['r']**2
        disc = b*b - c
        t = np.full(O.shape[0], np.inf)
        m = disc > 0
        sq = np.sqrt(np.where(m, disc, 0.0))
        t0 = -b - sq
        t1 = -b + sq
        cand = np.where(t0 > 1e-4, t0, np.where(t1 > 1e-4, t1, np.inf))
        t = np.where(m, cand, np.inf)
        return t
    else:  # plane
        denom = D @ obj['n']
        t = np.full(O.shape[0], np.inf)
        m = np.abs(denom) > 1e-6
        tt = -((O - obj['p']) @ obj['n']) / np.where(m, denom, 1.0)
        t = np.where(m & (tt > 1e-4), tt, np.inf)
        return t

def normal_at(obj, P):
    if obj['kind'] == 'sphere':
        return norm(P - obj['c'])
    else:
        return np.broadcast_to(obj['n'], P.shape).copy()

def albedo_at(obj, P):
    if obj['kind'] == 'sphere':
        return np.broadcast_to(obj['color'], P.shape).copy()
    # checker on plane (neon grid floor)
    u = P[:, 0]; v = P[:, 2]
    chk = ((np.floor(u*0.5) + np.floor(v*0.5)).astype(int) & 1).astype(bool)
    out = np.where(chk[:, None], obj['c1'], obj['c2'])
    return out

# ---- tracing ----------------------------------------------------------
def trace(objs, lights, O, D, depth, ambient, bg_top, bg_bot):
    n = O.shape[0]
    color = np.zeros((n, 3))
    tmin = np.full(n, np.inf)
    hit = np.full(n, -1)
    for i, obj in enumerate(objs):
        t = intersect(obj, O, D)
        closer = t < tmin
        tmin = np.where(closer, t, tmin)
        hit = np.where(closer, i, hit)

    miss = hit < 0
    # background = vertical neon gradient based on ray pitch.
    # Bias toward dark so the horizon melts into the near-black UI canvas.
    if miss.any():
        tt = np.clip(D[:, 1]*0.7 + 0.18, 0, 1)[:, None] ** 1.6
        color[miss] = (bg_bot*(1-tt) + bg_top*tt)[miss]

    idx = np.where(~miss)[0]
    if len(idx) == 0:
        return color

    for i, obj in enumerate(objs):
        sel = idx[hit[idx] == i]
        if len(sel) == 0:
            continue
        Os = O[sel]; Ds = D[sel]; ts = tmin[sel][:, None]
        P = Os + Ds*ts
        N = normal_at(obj, P)
        alb = albedo_at(obj, P)
        view = -Ds

        local = ambient * alb
        if obj.get('emissive') is not None:
            local = local + obj['emissive']

        for L in lights:
            Lpos = L['pos']; Lcol = L['color']
            Ldir = norm(Lpos - P)
            # shadow ray
            shadow = np.zeros(len(sel), bool)
            for o2 in objs:
                if o2 is obj and o2['kind'] == 'plane':
                    continue
                st = intersect(o2, P + N*1e-3, Ldir)
                dist = np.linalg.norm(Lpos - P, axis=1)
                shadow |= st < dist
            lit = (~shadow).astype(float)[:, None]
            diff = np.clip(np.einsum('ij,ij->i', N, Ldir), 0, 1)[:, None]
            # Blinn-Phong specular
            H = norm(Ldir + view)
            spec = np.clip(np.einsum('ij,ij->i', N, H), 0, 1)[:, None] ** 64
            local = local + lit * (alb*diff*Lcol + obj.get('spec', 0.4)*spec*Lcol)

        # reflection
        refl_k = obj.get('reflect', 0.0)
        if depth > 0 and refl_k > 0:
            R = Ds - 2*np.einsum('ij,ij->i', Ds, N)[:, None]*N
            rc = trace(objs, lights, P + N*1e-3, norm(R), depth-1, ambient, bg_top, bg_bot)
            # Fresnel-ish schlick
            cosv = np.clip(np.einsum('ij,ij->i', view, N), 0, 1)[:, None]
            fres = refl_k + (1-refl_k)*(1-cosv)**5
            local = local*(1-fres) + rc*fres

        color[sel] = local
    return color

def render(scene, W, H, samples=2):
    objs = scene['objs']; lights = scene['lights']
    cam = scene['cam']; look = scene['look']
    ambient = scene['ambient']; bg_top = scene['bg_top']; bg_bot = scene['bg_bot']
    fov = scene.get('fov', 50)

    fwd = normalize(look - cam)
    right = normalize(np.cross(fwd, np.array([0, 1.0, 0])))
    up = np.cross(right, fwd)
    aspect = W / H
    scale = math.tan(math.radians(fov*0.5))

    img = np.zeros((H, W, 3))
    # supersample grid
    offs = [(0.25, 0.25), (0.75, 0.75), (0.25, 0.75), (0.75, 0.25)][:samples*samples]
    if not offs:
        offs = [(0.5, 0.5)]
    ys, xs = np.mgrid[0:H, 0:W]
    O = np.broadcast_to(cam, (H*W, 3)).reshape(-1, 3).copy()
    for (ox, oy) in offs:
        px = (2*((xs+ox)/W)-1)*aspect*scale
        py = (1-2*((ys+oy)/H))*scale
        D = norm((fwd + px[..., None]*right + py[..., None]*up).reshape(-1, 3))
        col = trace(objs, lights, O, D, scene.get('depth', 3), ambient, bg_top, bg_bot)
        img += col.reshape(H, W, 3)
    img /= len(offs)

    # cheap bloom: blur bright areas and add back for neon glow
    lum = img.mean(axis=2, keepdims=True)
    bright = np.clip(img - 0.55, 0, None) * (lum > 0.5)
    bl = bright.copy()
    for _ in range(3):  # separable box blur passes
        bl = (np.pad(bl, ((1, 1), (0, 0), (0, 0)), mode='edge')[:-2]
              + bl
              + np.pad(bl, ((1, 1), (0, 0), (0, 0)), mode='edge')[2:]) / 3
        bl = (np.pad(bl, ((0, 0), (1, 1), (0, 0)), mode='edge')[:, :-2]
              + bl
              + np.pad(bl, ((0, 0), (1, 1), (0, 0)), mode='edge')[:, 2:]) / 3
    img = img + bl*0.8

    # tone map (Reinhard) + gamma
    img = img / (img + 1.0)
    img = np.clip(img, 0, 1) ** (1/2.2)
    return Image.fromarray((img*255).astype(np.uint8), 'RGB')

# ---- palette (Futuristic Neon tokens) ---------------------------------
CYAN = [0.18, 0.89, 1.0]
MAGENTA = [1.0, 0.31, 0.84]
VIOLET = [0.61, 0.42, 1.0]
GREEN = [0.24, 0.95, 0.63]
DARK = [0.027, 0.043, 0.086]

def hero_scene():
    objs = [
        make_plane([0, -1, 0], [0, 1, 0], [0.008, 0.03, 0.06], [0.015, 0.05, 0.10], reflect=0.6),
        make_sphere([0.0, 0.2, 0.0], 1.2, [0.04, 0.16, 0.24], reflect=0.55, spec=1.0,
                    emissive=[c*0.45 for c in CYAN]),
        make_sphere([-2.6, -0.2, 0.6], 0.8, [0.10, 0.03, 0.11], reflect=0.5, spec=1.0,
                    emissive=[c*0.42 for c in MAGENTA]),
        make_sphere([2.5, -0.35, 0.9], 0.65, [0.06, 0.04, 0.14], reflect=0.5, spec=1.0,
                    emissive=[c*0.38 for c in VIOLET]),
        make_sphere([1.1, -0.55, -1.4], 0.45, [0.03, 0.11, 0.08], reflect=0.45, spec=0.9,
                    emissive=[c*0.34 for c in GREEN]),
    ]
    lights = [
        dict(pos=np.array([5.0, 6.0, 4.0]), color=np.array(CYAN)*1.2),
        dict(pos=np.array([-6.0, 4.0, 2.0]), color=np.array(MAGENTA)*1.0),
        dict(pos=np.array([0.0, 5.0, -6.0]), color=np.array(VIOLET)*0.8),
    ]
    return dict(objs=objs, lights=lights,
                cam=np.array([0.0, 1.1, 6.2]), look=np.array([0.0, 0.0, 0.0]),
                ambient=0.04, bg_top=np.array([0.02, 0.05, 0.11]),
                bg_bot=np.array([0.008, 0.014, 0.035]), fov=50, depth=3)

def badge_scene():
    objs = [
        make_plane([0, -1, 0], [0, 1, 0], [0.02, 0.06, 0.12], [0.03, 0.10, 0.18], reflect=0.55),
        make_sphere([0.0, 0.05, 0.0], 1.25, [0.05, 0.20, 0.28], reflect=0.6, spec=1.0,
                    emissive=[c*0.22 for c in CYAN]),
        make_sphere([-1.0, -0.55, 1.0], 0.4, [0.10, 0.04, 0.12], reflect=0.5, spec=0.9,
                    emissive=[c*0.18 for c in MAGENTA]),
    ]
    lights = [
        dict(pos=np.array([4.0, 5.0, 4.0]), color=np.array(CYAN)*1.2),
        dict(pos=np.array([-5.0, 3.0, 3.0]), color=np.array(MAGENTA)*1.0),
    ]
    return dict(objs=objs, lights=lights,
                cam=np.array([0.0, 0.9, 5.0]), look=np.array([0.0, -0.1, 0.0]),
                ambient=0.07, bg_top=np.array([0.05, 0.10, 0.20]),
                bg_bot=np.array([0.02, 0.03, 0.07]), fov=46, depth=3)

if __name__ == '__main__':
    which = sys.argv[1] if len(sys.argv) > 1 else 'hero'
    out = sys.argv[2] if len(sys.argv) > 2 else f'{which}.png'
    W = int(sys.argv[3]) if len(sys.argv) > 3 else 1600
    H = int(sys.argv[4]) if len(sys.argv) > 4 else 640
    scene = hero_scene() if which == 'hero' else badge_scene()
    im = render(scene, W, H, samples=2)
    im.save(out)
    print('wrote', out, im.size)
