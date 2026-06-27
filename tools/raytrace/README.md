# Neon ray-tracer (UI imagery)

Generates the genuine ray-traced PNG assets used by the Trustlist Web UI
(`src/Trustlist.Web/wwwroot/img/neon-hero.png`, `neon-badge.png`).

A small recursive ray tracer (reflections, shadows, Blinn-Phong specular,
Fresnel, bloom, Reinhard tone-map) renders glossy neon spheres on a
reflective floor in the Futuristic-Neon palette — evoking a credential
trust network.

## Regenerate

```bash
pip install numpy pillow
python3 trace.py hero  ../../src/Trustlist.Web/wwwroot/img/neon-hero.png  1600 560
python3 trace.py badge ../../src/Trustlist.Web/wwwroot/img/neon-badge.png 640 640
```

Visual layer only — not referenced by application code at runtime.
