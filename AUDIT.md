# SimPlasPost Repository Audit

**Date:** 2026-04-13
**Scope:** Full repository audit covering code quality, correctness, security, performance, and project infrastructure.
**File audited:** `fe-postprocessor.jsx` (1,501 lines) — the sole source file.

---

## 1. Project Overview

SimPlasPost is a single-file React component (~1,500 LOC) that provides an interactive 3D finite element mesh post-processor. It supports:

- Ensight Gold ASCII format parsing (.case, .geo, .scl, .vec)
- Three.js-based 3D visualization with orthographic camera
- Scalar field contour plots, contour lines, and wireframe modes
- Deformation visualization
- Vector export (SVG, EPS, PDF) with hidden-line removal
- Mouse/touch orbit controls with momentum
- Demo meshes (Plate with Hole, 3D Beam, 2D Triangle)

---

## 2. Bugs and Correctness Issues

### 2.1 CRITICAL: PDF export rotation matrix is incorrect
**Location:** Line 803
```js
stream.push(`BT /F1 14 Tf ${bx+bw+16} ${byBot+bh/2} Td 90 0 0 90 0 0 Tm (${fieldName}) Tj ET`);
```
The text matrix `90 0 0 90 0 0 Tm` does not rotate text 90 degrees. In PDF, `Tm` takes a matrix `[a b c d e f]` where rotation requires `cos(theta)`, `sin(theta)` values. This line scales the text 90x in both directions instead of rotating it. The correct rotation matrix for 90 degrees would be `0 1 -1 0 x y Tm`.

### 2.2 HIGH: SVG font-family attribute missing quotes
**Location:** Lines 671, 673
```js
lines.push(`<text ... font-family=${CM_FONT_FAMILY} ...>`);
```
The `font-family` attribute value is not wrapped in quotes. Since `CM_FONT_FAMILY` contains spaces and commas, the generated SVG attribute will be malformed. Should be `font-family="${CM_FONT_FAMILY}"`.

### 2.3 HIGH: Stack overflow on large meshes
**Location:** Lines 1257, 1272-1273
```js
const zr = Math.max(...geo.nodes.map(n => n[2])) - Math.min(...geo.nodes.map(n => n[2]));
```
The spread operator (`...`) passes all node values as function arguments. JavaScript engines have a maximum call stack size (typically ~100,000 arguments). For meshes with more than ~100K nodes, this will throw `RangeError: Maximum call stack size exceeded`. Use a loop or `reduce()` instead.

### 2.4 MEDIUM: Texture memory leak in Three.js cleanup
**Location:** Lines 1095-1107 (triad label textures), Line 1149-1151 (cleanup)
The cleanup function disposes geometries and materials but does not dispose `CanvasTexture` objects created for the axis labels. Each scene rebuild leaks GPU texture memory. Also, the `WebGLRenderer` itself (stored in `sceneRef.current.renderer`) is never disposed on unmount.

### 2.5 MEDIUM: `velRef` referenced before its declaration
**Location:** Line 1114 uses `velRef.current`, but `velRef` is declared at line 1163.
This works due to JavaScript's execution model (useEffect runs after render), but the code ordering is confusing and brittle. If refactored, this could easily become a real bug.

### 2.6 MEDIUM: State update inside effect causes extra renders
**Location:** Line 981
```js
if(Math.abs(fmax-fmin)<1e-15) fmax=fmin+1; setFRange([fmin,fmax]);
```
`setFRange` is called inside the main `useEffect`, which depends on other state. This can trigger an additional render cycle each time the effect runs.

### 2.7 LOW: Silent fallback on malformed parser input
**Locations:** Lines 200-230, 235-270
The Ensight parsers silently convert NaN values to 0 (via `||0` fallback) rather than reporting parse errors. Malformed files will produce corrupted visualizations with no warning to the user.

---

## 3. Security Issues

### 3.1 HIGH: Cross-site scripting (XSS) in SVG export via field names
**Location:** Lines 671, 673
```js
lines.push(`<text ...>${fieldName}</text>`);
```
Field names come from uploaded Ensight files (line 174: `name:m[4]`). A maliciously crafted .case file could set a field name to `<script>alert(1)</script>` or SVG event handlers like `" onload="alert(1)`. This string is interpolated directly into the SVG output without HTML entity escaping. If the exported SVG is opened in a browser, this constitutes an XSS vulnerability.

**Fix:** Escape `<`, `>`, `&`, `"`, `'` in all user-derived strings before SVG interpolation.

### 3.2 HIGH: PostScript/PDF injection via field names
**Location:** Lines 739, 743, 801
```js
ps.push(`(${fieldName}) ... show`);
```
Field names are inserted into PostScript string literals without escaping `(`, `)`, and `\` characters. A malicious field name containing `) (malicious-ps-code) (` could inject arbitrary PostScript commands. The same issue applies to the PDF content stream.

**Fix:** Escape `(`, `)`, `\` in strings embedded in EPS/PDF output.

### 3.3 MEDIUM: External CDN with unpinned version
**Location:** Line 1321
```js
link.href = "https://cdn.jsdelivr.net/gh/aaaakshat/cm-web-fonts@latest/fonts.css";
```
The `@latest` tag means any push to the `aaaakshat/cm-web-fonts` repository will automatically be loaded by this component. If that repository is compromised, malicious CSS could be injected (e.g., data exfiltration via CSS, UI redressing). Pin to a specific commit hash or self-host the font.

### 3.4 MEDIUM: Unbounded memory allocation from file input
**Location:** Line 200
```js
const npts = parseInt(next());
```
A malicious .geo file could specify `npts = 999999999`, causing the parser to attempt to allocate arrays of that size (lines 204-207), potentially crashing the browser tab. Add a reasonable upper bound check.

### 3.5 LOW: Unsanitized JSON parsed from storage
**Location:** Line 879
```js
const r = await window.storage.get("fe-saved-views");
if (r && r.value) setSavedViews(JSON.parse(r.value));
```
No schema validation on the parsed JSON. If storage is tampered with, unexpected data shapes could cause runtime errors or prototype pollution (depending on the storage backend).

---

## 4. Performance Issues

### 4.1 HIGH: Full Three.js scene rebuild on every parameter change
**Location:** Line 1153 (dependency array)
```js
}, [meshData, activeField, displayMode, showDef, defScale, contourN, userMin, userMax]);
```
Changing *any* visualization parameter (e.g., dragging the deformation scale slider, adjusting iso-levels, switching fields) tears down and rebuilds the entire Three.js scene — including geometry computation, boundary face extraction, contour calculation, and GPU buffer uploads. For large meshes this will cause significant frame drops.

**Recommendation:** Separate geometry computation from rendering. Cache boundary faces and reuse them. Update only what changed (e.g., vertex colors when switching fields, contour geometry when changing levels).

### 4.2 MEDIUM: No Web Worker usage for computation
All parsing and geometry processing (boundary extraction, feature edges, contour computation, z-buffer rasterization for export) runs on the main thread. For meshes with >50K elements, this will block the UI.

### 4.3 MEDIUM: Contour slider causes continuous rebuilds
Moving the iso-levels range slider (line 1353) fires `onChange` on every pixel of movement, each triggering a full scene rebuild. Should debounce the input or use `onMouseUp`/`onPointerUp`.

### 4.4 LOW: Event handlers recreated every render
Mouse/touch handlers (`onMD`, `onMM`, `onMU`, `onWH`, `onTS`, `onTM`, `onTE`, `handleEnsight`, `handleJSON`) are plain arrow functions, not wrapped in `useCallback`. They're recreated on every render.

---

## 5. Code Quality and Maintainability

### 5.1 Monolithic single-file architecture
The entire application — parsers, mesh algorithms, rendering, camera math, export generators, UI component, and demo data — lives in one 1,501-line file. This makes the codebase difficult to navigate, test, review, and maintain.

**Recommendation:** Split into modules:
- `parsers/ensight.js` — Ensight format parsing
- `mesh/boundary.js` — Boundary face extraction, triangulation
- `mesh/features.js` — Feature edge detection
- `mesh/contours.js` — Contour line computation
- `rendering/camera.js` — Camera and projection math
- `rendering/zbuffer.js` — Software z-buffer
- `export/svg.js`, `export/eps.js`, `export/pdf.js`
- `demos/` — Demo mesh generators
- `FEPostprocessor.jsx` — Main component (UI only)

### 5.2 Dense, compressed code style
Many lines pack multiple statements, making the code hard to read and debug:
```js
// Line 287 — single line with complex math
const fv=nodes.map(([x,y])=>{const r=Math.sqrt(x*x+y*y),th=Math.atan2(y,x); return Math.max(0.2,Math.min(3.2,1+0.5*(0.09)/(r*r)*(1+Math.cos(2*th))));});
```

### 5.3 Magic numbers
Hard-coded numeric constants throughout with no documentation:
- `20` degrees for feature edge angle (line 117)
- `1e-14`, `1e-12`, `1e-15` — various epsilon tolerances
- `0.015` — z-buffer bias (line 525)
- `fd=20` — camera far distance (line 1124)
- `0.92`, `1.08` — zoom factors (line 1179)
- `0.75, 0.78, 0.82` — default face color (line 566)

### 5.4 Inline styles throughout JSX
The entire UI uses inline `style={{...}}` objects (lines 1329-1497), with style objects constructed on every render. This makes the styling hard to maintain and prevents CSS caching.

### 5.5 No TypeScript
Complex data structures (mesh data, camera params, face tables, export scenes) have no type definitions, making it easy to pass wrong shapes between functions.

---

## 6. Project Infrastructure

| Area | Status | Severity |
|------|--------|----------|
| `package.json` | Missing | **Critical** — cannot install dependencies, no version, no scripts |
| `.gitignore` | Missing | **High** — risk of committing node_modules, OS files, etc. |
| License | Missing | **High** — unclear legal status for contributors/users |
| Tests | None | **High** — zero test coverage |
| CI/CD | None | **Medium** — no automated quality checks |
| Linting | None (no ESLint/Prettier config) | **Medium** |
| TypeScript | None | **Medium** |
| Documentation | 2-line README only | **Medium** |
| Build config | None (Webpack/Vite/Rollup) | **Medium** — component cannot be independently built |

---

## 7. Summary of Findings

| Category | Critical | High | Medium | Low |
|----------|----------|------|--------|-----|
| Bugs | 1 | 2 | 3 | 1 |
| Security | 0 | 2 | 2 | 1 |
| Performance | 0 | 1 | 2 | 1 |
| Infrastructure | 1 | 3 | 4 | 0 |
| **Total** | **2** | **8** | **11** | **3** |

### Top Priority Fixes

1. **Fix PDF text rotation matrix** (bug, line 803) — PDF export produces garbled field name labels
2. **Fix SVG font-family quoting** (bug, lines 671/673) — SVG export produces invalid markup
3. **Fix stack overflow on large meshes** (bug, lines 1257/1272) — use `reduce()` instead of spread
4. **Escape field names in SVG/EPS/PDF export** (security) — prevents injection attacks from crafted input files
5. **Pin external CDN version** (security, line 1321) — prevent supply chain compromise
6. **Add `package.json`** (infra) — basic project metadata and dependency management
7. **Add `.gitignore`** (infra) — prevent accidental commits of generated files
8. **Add a license** (infra) — clarify usage rights

---

## 8. Positive Observations

- Solid domain knowledge: correct boundary face extraction, marching-triangle contouring, Chaikin subdivision, painter's algorithm export
- Clean orthographic camera implementation with pole-compensated orbit controls and momentum
- Ensight Gold format parser handles multiple element types correctly
- Hidden-line removal via software z-buffer for vector exports is a nice feature
- Good touch gesture support (rotate, pan, pinch zoom)
- Persistent saved views is a thoughtful UX addition
- The color bar and labeling in exports gives publication-quality output
