import { useState, useEffect, useRef, useCallback, useMemo } from "react";
import * as THREE from "three";

// ─── Google Turbo Colormap ───
// High-resolution LUT from the original Turbo paper (Mikhailov, 2019)
const TURBO = [
  [0.000, 0.190, 0.072, 0.232],
  [0.033, 0.234, 0.141, 0.414],
  [0.067, 0.266, 0.220, 0.576],
  [0.100, 0.282, 0.310, 0.714],
  [0.133, 0.278, 0.408, 0.827],
  [0.167, 0.254, 0.510, 0.906],
  [0.200, 0.210, 0.613, 0.946],
  [0.233, 0.156, 0.710, 0.948],
  [0.267, 0.117, 0.795, 0.910],
  [0.300, 0.117, 0.864, 0.836],
  [0.333, 0.172, 0.914, 0.733],
  [0.367, 0.270, 0.948, 0.608],
  [0.400, 0.393, 0.966, 0.478],
  [0.433, 0.520, 0.970, 0.353],
  [0.467, 0.643, 0.960, 0.243],
  [0.500, 0.755, 0.936, 0.157],
  [0.533, 0.849, 0.898, 0.098],
  [0.567, 0.919, 0.847, 0.063],
  [0.600, 0.964, 0.784, 0.050],
  [0.633, 0.987, 0.711, 0.051],
  [0.667, 0.993, 0.631, 0.060],
  [0.700, 0.985, 0.546, 0.068],
  [0.733, 0.966, 0.459, 0.068],
  [0.767, 0.938, 0.373, 0.060],
  [0.800, 0.902, 0.291, 0.047],
  [0.833, 0.858, 0.215, 0.036],
  [0.867, 0.806, 0.149, 0.024],
  [0.900, 0.743, 0.098, 0.015],
  [0.933, 0.670, 0.062, 0.010],
  [0.967, 0.586, 0.042, 0.008],
  [1.000, 0.480, 0.015, 0.010],
];
function sampleTurbo(t) {
  t = Math.max(0, Math.min(1, t));
  for (let i = 0; i < TURBO.length - 1; i++) {
    if (t <= TURBO[i+1][0]) {
      const f = (t - TURBO[i][0]) / (TURBO[i+1][0] - TURBO[i][0]);
      return [TURBO[i][1]+f*(TURBO[i+1][1]-TURBO[i][1]), TURBO[i][2]+f*(TURBO[i+1][2]-TURBO[i][2]), TURBO[i][3]+f*(TURBO[i+1][3]-TURBO[i][3])];
    }
  }
  const l = TURBO[TURBO.length-1]; return [l[1],l[2],l[3]];
}

// ─── Face tables per element type ───
const FACE_TABLE = {
  tri3:  { faces: c => [c], dim: 2 },
  tria3: { faces: c => [c], dim: 2 },
  quad4: { faces: c => [c], dim: 2 },
  tet4:  { faces: c => [[c[0],c[2],c[1]],[c[0],c[1],c[3]],[c[1],c[2],c[3]],[c[0],c[3],c[2]]], dim: 3 },
  tetra4:{ faces: c => [[c[0],c[2],c[1]],[c[0],c[1],c[3]],[c[1],c[2],c[3]],[c[0],c[3],c[2]]], dim: 3 },
  hex8:  { faces: c => [
    [c[0],c[3],c[2],c[1]], [c[4],c[5],c[6],c[7]],
    [c[0],c[1],c[5],c[4]], [c[2],c[3],c[7],c[6]],
    [c[0],c[4],c[7],c[3]], [c[1],c[2],c[6],c[5]],
  ], dim: 3 },
  hexa8: { faces: c => [
    [c[0],c[3],c[2],c[1]], [c[4],c[5],c[6],c[7]],
    [c[0],c[1],c[5],c[4]], [c[2],c[3],c[7],c[6]],
    [c[0],c[4],c[7],c[3]], [c[1],c[2],c[6],c[5]],
  ], dim: 3 },
  penta6:{ faces: c => [
    [c[0],c[2],c[1]], [c[3],c[4],c[5]],
    [c[0],c[1],c[4],c[3]], [c[1],c[2],c[5],c[4]], [c[0],c[3],c[5],c[2]],
  ], dim: 3 },
};

const ENSIGHT_ETYPE = {
  tria3:"tri3", tria6:"tri3", quad4:"quad4", quad8:"quad4",
  tetra4:"tet4", tetra10:"tet4", hexa8:"hex8", hexa20:"hex8", penta6:"penta6", penta15:"penta6",
};
const ENSIGHT_NPN = {
  point:1, bar2:2, bar3:3, tria3:3, tria6:6, quad4:4, quad8:8,
  tetra4:4, tetra10:10, hexa8:8, hexa20:20, penta6:6, penta15:15,
};
const CORNER_COUNT = {
  tria3:3, tria6:3, quad4:4, quad8:4, tetra4:4, tetra10:4, hexa8:8, hexa20:8, penta6:6, penta15:6,
};

// ─── Boundary face extraction ───
function extractBoundaryFaces(elements, is3D) {
  if (!is3D) {
    const faces = [];
    for (const el of elements) {
      const ft = FACE_TABLE[el.type]; if (!ft) continue;
      for (const f of ft.faces(el.conn)) faces.push(f);
    }
    return faces;
  }
  // 3D: keep faces that appear exactly once
  const faceCount = new Map();
  const faceList = [];
  for (const el of elements) {
    const ft = FACE_TABLE[el.type]; if (!ft || ft.dim !== 3) continue;
    for (const f of ft.faces(el.conn)) {
      const key = [...f].sort((a,b)=>a-b).join(",");
      faceCount.set(key, (faceCount.get(key)||0)+1);
      faceList.push([key, f]);
    }
  }
  return faceList.filter(([k]) => faceCount.get(k) === 1).map(([,f]) => f);
}

function triangulateFace(f) {
  if (f.length===3) return [f];
  if (f.length===4) return [[f[0],f[1],f[2]],[f[0],f[2],f[3]]];
  const t=[]; for (let i=1;i<f.length-1;i++) t.push([f[0],f[i],f[i+1]]); return t;
}

// ─── Feature Edge Detection (dihedral angle heuristic) ───
function extractFeatureEdges(bfaces, dp, angleDeg) {
  const cosThresh = Math.cos((angleDeg || 20) * Math.PI / 180);
  // Compute face normals
  const fNormals = bfaces.map(face => {
    const p0=dp[face[0]], p1=dp[face[1]], p2=dp[face[face.length>2?2:1]];
    const ux=p1[0]-p0[0], uy=p1[1]-p0[1], uz=p1[2]-p0[2];
    const vx=p2[0]-p0[0], vy=p2[1]-p0[1], vz=p2[2]-p0[2];
    const nx=uy*vz-uz*vy, ny=uz*vx-ux*vz, nz=ux*vy-uy*vx;
    const l=Math.sqrt(nx*nx+ny*ny+nz*nz);
    return l>1e-14?[nx/l,ny/l,nz/l]:[0,0,1];
  });
  // Build edge→face adjacency
  const edgeFaces = new Map();
  for (let fi=0; fi<bfaces.length; fi++) {
    const face=bfaces[fi];
    for (let j=0; j<face.length; j++) {
      const a=face[j], b=face[(j+1)%face.length];
      const key=a<b?a+","+b:b+","+a;
      if (!edgeFaces.has(key)) edgeFaces.set(key,[]);
      edgeFaces.get(key).push(fi);
    }
  }
  // Keep boundary edges + edges where dihedral angle > threshold
  const featurePos = []; // flat [ax,ay,az, bx,by,bz, ...]
  for (const [key, fis] of edgeFaces) {
    let keep = false;
    if (fis.length === 1) { keep = true; } // boundary
    else if (fis.length === 2) {
      const n1=fNormals[fis[0]], n2=fNormals[fis[1]];
      const dot=n1[0]*n2[0]+n1[1]*n2[1]+n1[2]*n2[2];
      if (dot < cosThresh) keep = true; // sharp angle
    }
    if (keep) {
      const [sa,sb] = key.split(",");
      const a=dp[parseInt(sa)], b=dp[parseInt(sb)];
      featurePos.push(a[0],a[1],a[2], b[0],b[1],b[2]);
    }
  }
  return featurePos;
}

// ─── Ensight Gold ASCII Parser ───
function parseEnsightCase(txt) {
  const r = { geoFile:null, variables:[] };
  const lines = txt.split("\n").map(l=>l.trim());
  let sec = "";
  for (const line of lines) {
    if (/^FORMAT/i.test(line)) { sec="f"; continue; }
    if (/^GEOMETRY/i.test(line)) { sec="g"; continue; }
    if (/^VARIABLE/i.test(line)) { sec="v"; continue; }
    if (/^TIME/i.test(line)) { sec="t"; continue; }
    if (sec==="g" && /^model:/i.test(line)) {
      const parts = line.replace(/^model:\s*/i,"").trim().split(/\s+/);
      // May have time set number before filename: "model: 1 geometry.geo"
      r.geoFile = parts.length > 1 && /^\d+$/.test(parts[0]) ? parts[1] : parts[0];
    }
    if (sec==="v") {
      const m = line.match(/^(scalar|vector)\s+per\s+(node|element):\s*(?:(\d+)\s+)?(\S+)\s+(\S+)/i);
      if (m) r.variables.push({vtype:m[1].toLowerCase(),loc:m[2].toLowerCase(),name:m[4],file:m[5]});
    }
  }
  return r;
}

// Upper bounds prevent a malicious or corrupt Ensight file from requesting huge
// allocations and crashing the tab before the parser reaches any real data.
const MAX_ENSIGHT_NODES = 50_000_000;
const MAX_ENSIGHT_ELEMS = 50_000_000;

function parseEnsightGeo(text) {
  const lines = text.split("\n");
  let i = 0;
  const next = () => (lines[i++] || "").trim();

  next(); next(); // description
  const nidLine = next().toLowerCase();
  const nidGiven = nidLine.includes("given");
  const eidLine = next().toLowerCase();
  const eidGiven = eidLine.includes("given");

  const allNodes = [], allElements = [];

  while (i < lines.length) {
    const line = next();
    if (line.toLowerCase() !== "part") continue;
    next(); // part number
    next(); // part description
    const coordLine = next();
    if (!/^coordinates/i.test(coordLine)) continue;
    const npts = parseInt(next());
    if (isNaN(npts) || npts <= 0) continue;
    if (npts > MAX_ENSIGHT_NODES) throw new Error(`Ensight part declares ${npts} nodes (> ${MAX_ENSIGHT_NODES}); refusing to allocate.`);
    const remaining = lines.length - i;
    if (npts > remaining) throw new Error(`Ensight part declares ${npts} nodes but only ${remaining} lines remain.`);

    if (nidGiven) for (let k=0;k<npts;k++) next();
    const x=[],y=[],z=[];
    let badCoord=0;
    const readCoord=()=>{ const v=parseFloat(next()); if(isNaN(v)) badCoord++; return v; };
    for (let k=0;k<npts;k++) x.push(readCoord());
    for (let k=0;k<npts;k++) y.push(readCoord());
    for (let k=0;k<npts;k++) z.push(readCoord());
    if (badCoord>0) throw new Error(`Ensight geometry: ${badCoord} non-numeric coordinates in part starting near line ${i-3*npts+1}.`);

    const base = allNodes.length;
    for (let k=0;k<npts;k++) allNodes.push([x[k], y[k], z[k]]);

    // Element blocks
    while (i < lines.length) {
      const peek = (lines[i]||"").trim().toLowerCase();
      if (!peek || peek === "part") break;
      const etype = next().trim().toLowerCase();
      if (!ENSIGHT_NPN[etype]) break;
      const ne = parseInt(next());
      if (isNaN(ne) || ne<=0) break;
      if (ne > MAX_ENSIGHT_ELEMS) throw new Error(`Ensight block declares ${ne} elements (> ${MAX_ENSIGHT_ELEMS}); refusing to allocate.`);
      if (eidGiven) for (let k=0;k<ne;k++) next();
      const npe = ENSIGHT_NPN[etype];
      const mapped = ENSIGHT_ETYPE[etype] || etype;
      const corners = CORNER_COUNT[etype] || npe;
      if (mapped==="bar2" || mapped==="point") { for (let k=0;k<ne;k++) next(); continue; }
      for (let k=0;k<ne;k++) {
        const parts = next().split(/\s+/).map(Number);
        const conn = parts.slice(0, corners).map(n => n-1+base);
        allElements.push({type:mapped, conn});
      }
    }
  }
  return { nodes:allNodes, elements:allElements };
}

function parseEnsightScalar(text, nNodes) {
  const lines = text.split("\n");
  let i=0;
  const next = () => (lines[i++]||"").trim();
  next(); // description
  const vals = [];
  while (i<lines.length) {
    const l = next().toLowerCase();
    if (l==="part") { next(); next(); // part num + "coordinates"
      while (i<lines.length && vals.length<nNodes) {
        const v = parseFloat((lines[i]||"").trim());
        if (isNaN(v)) break;
        vals.push(v); i++;
      }
      break;
    }
  }
  return vals;
}

function parseEnsightVector(text, nNodes) {
  const lines = text.split("\n");
  let i=0;
  const next = () => (lines[i++]||"").trim();
  next(); // description
  const vx=[],vy=[],vz=[];
  while (i<lines.length) {
    const l = next().toLowerCase();
    if (l==="part") { next(); next();
      for (let k=0;k<nNodes&&i<lines.length;k++) vx.push(parseFloat(next())||0);
      for (let k=0;k<nNodes&&i<lines.length;k++) vy.push(parseFloat(next())||0);
      for (let k=0;k<nNodes&&i<lines.length;k++) vz.push(parseFloat(next())||0);
      break;
    }
  }
  return vx.map((x,k)=>[x, vy[k]||0, vz[k]||0]);
}

// ─── Demo meshes ───
function genPlateHole(opts={}) {
  const {nr=24, nth=64, no=24, name="Plate with Hole (2D Quads)"} = opts;
  const nodes=[],elements=[];
  const R=0.3,W=1;
  for (let j=0;j<=nth;j++) { const th=(j/nth)*Math.PI*0.5;
    for (let i=0;i<=nr+no;i++) { let r=i<=nr?R+(W*0.5-R)*(i/nr):W*0.5+(W-W*0.5)*((i-nr)/no);
      nodes.push([r*Math.cos(th),r*Math.sin(th),0]); }}
  const c=nr+no+1;
  for (let j=0;j<nth;j++) for (let i=0;i<nr+no;i++) {
    const n0=j*c+i; elements.push({type:"quad4",conn:[n0,n0+1,(j+1)*c+i+1,(j+1)*c+i]}); }
  const oN=nodes.length, oE=elements.length, mx={};
  let idx=oN;
  for (let i=0;i<oN;i++) { if(nodes[i][0]>1e-10){nodes.push([-nodes[i][0],nodes[i][1],0]);mx[i]=idx++;}else mx[i]=i; }
  for (let e=0;e<oE;e++) { const cn=elements[e].conn.map(n=>mx[n]); elements.push({type:"quad4",conn:[cn[1],cn[0],cn[3],cn[2]]}); }
  const fv=nodes.map(([x,y])=>{const r=Math.sqrt(x*x+y*y),th=Math.atan2(y,x); return Math.max(0.2,Math.min(3.2,1+0.5*(0.09)/(r*r)*(1+Math.cos(2*th))));});
  const dv=nodes.map(([x,y])=>{const f=Math.max(0,1-Math.sqrt(x*x+y*y))*0.05; return [x*f*0.5,y*f,0];});
  return {name,dim:2,nodes,elements,fields:{"Von Mises":{type:"scalar",values:fv},Displacement:{type:"vector",values:dv}}};
}
function gen3DBeam(opts={}) {
  const {nx=48, ny=10, nz=10, name="Cantilever (3D Hex8)"} = opts;
  const Lx=4,Ly=.5,Lz=.5,nodes=[],elements=[];
  for(let k=0;k<=nz;k++)for(let j=0;j<=ny;j++)for(let i=0;i<=nx;i++) nodes.push([(i/nx)*Lx,(j/ny)*Ly-Ly/2,(k/nz)*Lz-Lz/2]);
  const id=(i,j,k)=>k*(ny+1)*(nx+1)+j*(nx+1)+i;
  for(let k=0;k<nz;k++)for(let j=0;j<ny;j++)for(let i=0;i<nx;i++)
    elements.push({type:"hex8",conn:[id(i,j,k),id(i+1,j,k),id(i+1,j+1,k),id(i,j+1,k),id(i,j,k+1),id(i+1,j,k+1),id(i+1,j+1,k+1),id(i,j+1,k+1)]});
  return {name,dim:3,nodes,elements,fields:{
    "Bending Stress":{type:"scalar",values:nodes.map(([x,y])=>Math.max(0,y*(Lx-x)*4+.5))},
    Displacement:{type:"vector",values:nodes.map(([x])=>{const t=x/Lx;return[0,-.15*t*t*(3-2*t),0];})}}};
}
function gen2DTri(opts={}) {
  const {n=40, name="Unit Square (2D Tri3)"} = opts;
  const nodes=[],elements=[];
  for(let j=0;j<=n;j++)for(let i=0;i<=n;i++) nodes.push([i/n,j/n,0]);
  for(let j=0;j<n;j++)for(let i=0;i<n;i++){const b=j*(n+1)+i;
    elements.push({type:"tri3",conn:[b,b+1,(j+1)*(n+1)+i+1]});
    elements.push({type:"tri3",conn:[b,(j+1)*(n+1)+i+1,(j+1)*(n+1)+i]});}
  const fv=nodes.map(([x,y])=>Math.sin(Math.PI*x)*Math.sin(Math.PI*y));
  const dv=nodes.map(([x,y])=>[.03*Math.sin(Math.PI*x)*Math.sin(Math.PI*y),.05*Math.sin(Math.PI*x)*Math.sin(Math.PI*y),0]);
  return {name,dim:2,nodes,elements,fields:{Temperature:{type:"scalar",values:fv},Displacement:{type:"vector",values:dv}}};
}

// ─── Line Thickness Presets ───
const LINE_PRESETS = [
  { name: "Hairline", svgW: 0.08, opacity: 0.07 },
  { name: "X-Thin",   svgW: 0.15, opacity: 0.13 },
  { name: "Thin",     svgW: 0.25, opacity: 0.22 },
  { name: "Medium",   svgW: 0.45, opacity: 0.35 },
  { name: "Thick",    svgW: 0.80, opacity: 0.55 },
  { name: "Bold",     svgW: 1.50, opacity: 0.80 },
];

function autoLineWeight(nEdges) {
  if (nEdges > 5000) return LINE_PRESETS[0]; // Hairline
  if (nEdges > 2000) return LINE_PRESETS[1]; // X-Thin
  if (nEdges > 800)  return LINE_PRESETS[2]; // Thin
  if (nEdges > 300)  return LINE_PRESETS[3]; // Medium
  if (nEdges > 100)  return LINE_PRESETS[4]; // Thick
  return LINE_PRESETS[5];                     // Bold
}

// ─── Contour Lines (marching triangles) ───
function computeContourSegments(bfaces, dp, fv, fmin, fmax, nLevels) {
  if (!fv || nLevels < 1) return [];
  const levels = [];
  for (let i = 1; i <= nLevels; i++) levels.push(fmin + i * (fmax - fmin) / (nLevels + 1));
  const segs = []; // each: [pointA, pointB, levelValue, perpVector]
  for (const face of bfaces) {
    const tris = triangulateFace(face);
    for (const tri of tris) {
      const [n0,n1,n2] = tri;
      const f0=fv[n0], f1=fv[n1], f2=fv[n2];
      const p0=dp[n0], p1=dp[n1], p2=dp[n2];
      // Face normal
      const e1=[p1[0]-p0[0],p1[1]-p0[1],p1[2]-p0[2]];
      const e2=[p2[0]-p0[0],p2[1]-p0[1],p2[2]-p0[2]];
      const fn=[e1[1]*e2[2]-e1[2]*e2[1], e1[2]*e2[0]-e1[0]*e2[2], e1[0]*e2[1]-e1[1]*e2[0]];
      for (const lv of levels) {
        const cx = [];
        const edges = [[f0,f1,p0,p1],[f1,f2,p1,p2],[f2,f0,p2,p0]];
        for (const [fa,fb,pa,pb] of edges) {
          if ((fa - lv) * (fb - lv) < 0) {
            const t = (lv - fa) / (fb - fa);
            cx.push([pa[0]+t*(pb[0]-pa[0]), pa[1]+t*(pb[1]-pa[1]), pa[2]+t*(pb[2]-pa[2])]);
          }
        }
        if (cx.length === 2) {
          // Perpendicular in face plane: cross(seg_dir, face_normal), normalized
          const sd=[cx[1][0]-cx[0][0], cx[1][1]-cx[0][1], cx[1][2]-cx[0][2]];
          const px=sd[1]*fn[2]-sd[2]*fn[1], py=sd[2]*fn[0]-sd[0]*fn[2], pz=sd[0]*fn[1]-sd[1]*fn[0];
          const pl=Math.sqrt(px*px+py*py+pz*pz);
          const perp = pl>1e-14 ? [px/pl,py/pl,pz/pl] : [0,0,1];
          segs.push([cx[0], cx[1], lv, perp]);
        }
      }
    }
  }
  return segs;
}

// ─── Chain contour segments into polylines and apply Chaikin subdivision ───
function smoothContours(segs, nSubdiv) {
  if (segs.length === 0) return segs;
  // Group by level
  const byLv = new Map();
  for (const s of segs) {
    const k = s[2].toFixed(8);
    if (!byLv.has(k)) byLv.set(k, []);
    byLv.get(k).push(s);
  }
  const result = [];
  const hk = p => `${(p[0]*1e5|0)},${(p[1]*1e5|0)},${(p[2]*1e5|0)}`;

  for (const [, lvSegs] of byLv) {
    const lv = lvSegs[0][2];
    // Build adjacency: hash(endpoint) → [[segIdx, endIdx(0|1)], ...]
    const adj = new Map();
    for (let i = 0; i < lvSegs.length; i++) {
      const ka = hk(lvSegs[i][0]), kb = hk(lvSegs[i][1]);
      if (!adj.has(ka)) adj.set(ka, []);
      if (!adj.has(kb)) adj.set(kb, []);
      adj.get(ka).push([i, 0]);
      adj.get(kb).push([i, 1]);
    }
    // Chain into polylines
    const used = new Set();
    for (let si = 0; si < lvSegs.length; si++) {
      if (used.has(si)) continue;
      used.add(si);
      const chain = [lvSegs[si][0], lvSegs[si][1]];
      // Extend forward
      for (let safe=0; safe<1e5; safe++) {
        const k = hk(chain[chain.length-1]);
        const nb = adj.get(k); let found=false;
        if (nb) for (const [ni, ne] of nb) {
          if (used.has(ni)) continue;
          used.add(ni); chain.push(ne===0 ? lvSegs[ni][1] : lvSegs[ni][0]);
          found=true; break;
        }
        if (!found) break;
      }
      // Extend backward
      for (let safe=0; safe<1e5; safe++) {
        const k = hk(chain[0]);
        const nb = adj.get(k); let found=false;
        if (nb) for (const [ni, ne] of nb) {
          if (used.has(ni)) continue;
          used.add(ni); chain.unshift(ne===0 ? lvSegs[ni][1] : lvSegs[ni][0]);
          found=true; break;
        }
        if (!found) break;
      }
      // Chaikin subdivision
      let pts = chain;
      for (let iter = 0; iter < nSubdiv; iter++) {
        if (pts.length < 3) break;
        const np = [pts[0]];
        for (let i = 0; i < pts.length - 1; i++) {
          const [ax,ay,az]=pts[i],[bx,by,bz]=pts[i+1];
          np.push([ax*0.75+bx*0.25, ay*0.75+by*0.25, az*0.75+bz*0.25]);
          np.push([ax*0.25+bx*0.75, ay*0.25+by*0.75, az*0.25+bz*0.75]);
        }
        np.push(pts[pts.length-1]);
        pts = np;
      }
      // Convert polyline back to segments
      for (let i = 0; i < pts.length - 1; i++) {
        result.push([pts[i], pts[i+1], lv, [0,0,1]]);
      }
    }
  }
  return result;
}

// ─── Vector Export Utilities ───
function v3sub(a,b){return[a[0]-b[0],a[1]-b[1],a[2]-b[2]];}
function v3cross(a,b){return[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];}
function v3dot(a,b){return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];}
function v3norm(v){const l=Math.sqrt(v[0]*v[0]+v[1]*v[1]+v[2]*v[2]);return l>1e-12?[v[0]/l,v[1]/l,v[2]/l]:[0,0,1];}

function buildCamera(cp) {
  const eye = [
    cp.dist*Math.sin(cp.phi)*Math.sin(cp.theta)+cp.tx,
    cp.dist*Math.cos(cp.phi)+cp.ty,
    cp.dist*Math.sin(cp.phi)*Math.cos(cp.theta)
  ];
  const tgt = [cp.tx, cp.ty, 0];
  const fwd = v3norm(v3sub(tgt, eye));
  let up = [0,1,0];
  // Handle near-vertical views
  if (Math.abs(v3dot(fwd, up)) > 0.99) up = [0, 0, -1];
  const right = v3norm(v3cross(fwd, up));
  const upC = v3cross(right, fwd);
  return { eye, fwd, right, up: upC };
}

function projectVtx(v, cam, orthoHH, W, H) {
  const rel = v3sub(v, cam.eye);
  const x = v3dot(rel, cam.right);
  const y = v3dot(rel, cam.up);
  const z = v3dot(rel, cam.fwd);
  if (z < 0.01) return null; // behind camera
  const scale = H / (2 * orthoHH);
  return [W/2 + x * scale, H/2 - y * scale, z];
}

// ─── Software Z-buffer for hidden line removal in exports ───
function rasterTri(zbuf, W, H, p0, p1, p2) {
  let [a,b,c] = [p0,p1,p2].sort((x,y) => x[1]-y[1]);
  const yStart = Math.max(0, Math.ceil(a[1]));
  const yEnd = Math.min(H-1, Math.floor(c[1]));
  for (let y = yStart; y <= yEnd; y++) {
    // Long edge a→c
    const tac = (c[1]-a[1]) > 0.001 ? (y-a[1])/(c[1]-a[1]) : 0;
    let x1 = a[0]+tac*(c[0]-a[0]), z1 = a[2]+tac*(c[2]-a[2]);
    let x2, z2;
    if (y < b[1]) {
      if (Math.abs(b[1]-a[1]) < 0.001) continue;
      const t = (y-a[1])/(b[1]-a[1]);
      x2 = a[0]+t*(b[0]-a[0]); z2 = a[2]+t*(b[2]-a[2]);
    } else {
      if (Math.abs(c[1]-b[1]) < 0.001) continue;
      const t = (y-b[1])/(c[1]-b[1]);
      x2 = b[0]+t*(c[0]-b[0]); z2 = b[2]+t*(c[2]-b[2]);
    }
    if (x1 > x2) { let tmp=x1;x1=x2;x2=tmp; tmp=z1;z1=z2;z2=tmp; }
    const sx = Math.max(0, Math.ceil(x1)), ex = Math.min(W-1, Math.floor(x2));
    const dx = x2-x1;
    for (let x = sx; x <= ex; x++) {
      const t = dx > 0.001 ? (x-x1)/dx : 0;
      const z = z1+t*(z2-z1);
      const idx = y*W+x;
      if (z < zbuf[idx]) zbuf[idx] = z;
    }
  }
}

function buildZBuffer(projFaces, W, H) {
  const zbuf = new Float32Array(W*H); zbuf.fill(1e10);
  for (const f of projFaces) {
    const p = f.pts3D;
    for (let i = 1; i < p.length-1; i++) rasterTri(zbuf, W, H, p[0], p[i], p[i+1]);
  }
  return zbuf;
}

function isSegmentVisible(a, b, zbuf, W, H) {
  // Sample 5 points along the segment; visible if majority are in front of z-buffer
  const N = 5; let vis = 0;
  for (let i = 0; i < N; i++) {
    const t = (i+0.5)/N;
    const x = Math.round(a[0]+t*(b[0]-a[0]));
    const y = Math.round(a[1]+t*(b[1]-a[1]));
    const z = a[2]+t*(b[2]-a[2]);
    if (x < 0||x >= W||y < 0||y >= H) continue;
    if (z <= zbuf[y*W+x]+0.015) vis++;
  }
  return vis >= Math.ceil(N/2);
}

function computeExportScene(meshData, activeField, showDef, defScale, camParams, W, H, dMode, cN, eMin, eMax) {
  if (!meshData) return null;
  const ns = meshData.nodes;
  const cen = [0,0,0], mn=[1e9,1e9,1e9], mx=[-1e9,-1e9,-1e9];
  for (const n of ns) for (let d=0;d<3;d++){mn[d]=Math.min(mn[d],n[d]);mx[d]=Math.max(mx[d],n[d]);}
  cen[0]=(mn[0]+mx[0])/2; cen[1]=(mn[1]+mx[1])/2; cen[2]=(mn[2]+mx[2])/2;
  const span=Math.max(mx[0]-mn[0],mx[1]-mn[1],mx[2]-mn[2],1e-12), sc=2/span;

  const is3D=meshData.dim===3||meshData.elements.some(e=>{const ft=FACE_TABLE[e.type];return ft&&ft.dim===3;});
  const dispF=meshData.fields?.Displacement||meshData.fields?.displacement;
  const dp=ns.map((n,i)=>{
    const d=(showDef&&dispF?.type==="vector")?dispF.values[i]:[0,0,0];
    return[(n[0]+d[0]*defScale-cen[0])*sc,(n[1]+d[1]*defScale-cen[1])*sc,(n[2]+d[2]*defScale-cen[2])*sc];
  });

  const field=meshData.fields?.[activeField];
  let fmin=0,fmax=1,fv=null;
  if(field?.type==="scalar"){fv=field.values;fmin=Infinity;fmax=-Infinity;for(const v of fv){if(v<fmin)fmin=v;if(v>fmax)fmax=v;}
    if(Math.abs(fmax-fmin)<1e-15)fmax=fmin+1;}
  // Use effective range (user overrides or auto)
  const efMin = eMin!=null ? eMin : fmin;
  const efMax = eMax!=null ? eMax : fmax;
  const efSpan = Math.abs(efMax-efMin)<1e-15 ? 1 : efMax-efMin;

  const bfaces = extractBoundaryFaces(meshData.elements, is3D);
  const cam = buildCamera(camParams);
  const oHH = camParams.dist;
  const exportFaces = [];
  const wireEdges3D = [];

  for (const face of bfaces) {
    const pts = face.map(ni => projectVtx(dp[ni], cam, oHH, W, H));
    if (pts.some(p => p === null)) continue;
    const screenPts = pts.map(p => [p[0], p[1]]);
    const pts3D = pts.map(p => [p[0], p[1], p[2]]);
    const avgZ = pts.reduce((s,p) => s + p[2], 0) / pts.length;
    let r=0.75,g=0.78,b=0.82;
    if (fv) {
      const avgF = face.reduce((s,ni) => s + fv[ni], 0) / face.length;
      const t = (avgF - efMin) / efSpan;
      [r,g,b] = sampleTurbo(t);
    }
    exportFaces.push({ screenPts, pts3D, r, g, b, depth: avgZ });
    if (dMode==="wireframe"||dMode==="plot") {
      for (let j=0;j<pts.length;j++) wireEdges3D.push([pts[j], pts[(j+1)%pts.length]]);
    }
  }
  exportFaces.sort((a,b) => b.depth - a.depth);

  const zbuf = buildZBuffer(exportFaces, W, H);
  const visibleEdges = [];

  // All edges for wireframe/plot modes
  if (dMode==="wireframe"||dMode==="plot") {
    for (const [a,b] of wireEdges3D) {
      if (isSegmentVisible(a, b, zbuf, W, H)) visibleEdges.push([[a[0],a[1]],[b[0],b[1]]]);
    }
  }
  // Feature edges only for contour-lines mode
  if (dMode==="lines") {
    const featPos = extractFeatureEdges(bfaces, dp, 20);
    for (let k=0; k<featPos.length; k+=6) {
      const a3=[featPos[k],featPos[k+1],featPos[k+2]], b3=[featPos[k+3],featPos[k+4],featPos[k+5]];
      const pa=projectVtx(a3,cam,oHH,W,H), pb=projectVtx(b3,cam,oHH,W,H);
      if (pa&&pb&&isSegmentVisible(pa,pb,zbuf,W,H)) visibleEdges.push([[pa[0],pa[1]],[pb[0],pb[1]]]);
    }
  }

  // Contour iso-lines for lines mode (with color from level)
  const contours = [];
  if (dMode==="lines" && fv) {
    const rawSegs = computeContourSegments(bfaces, dp, fv, efMin, efMax, cN);
    const csegs = smoothContours(rawSegs, 2);
    for (const [a,b,lv] of csegs) {
      const pa = projectVtx(a, cam, oHH, W, H);
      const pb = projectVtx(b, cam, oHH, W, H);
      if (pa && pb && isSegmentVisible(pa, pb, zbuf, W, H)) {
        const t=(lv-efMin)/efSpan;
        const [cr,cg,cb]=sampleTurbo(t);
        contours.push({p1:[pa[0],pa[1]], p2:[pb[0],pb[1]], r:cr, g:cg, b:cb});
      }
    }
  }

  // In "lines" mode: faces should be white, not field-colored
  if (dMode==="lines") {
    for (const f of exportFaces) { f.r=1.0; f.g=1.0; f.b=1.0; }
  }

  let nEdges = 0;
  for (const face of bfaces) nEdges += face.length;
  const lp = autoLineWeight(nEdges);
  return { faces: exportFaces, visibleEdges, contours, lp, fieldName: activeField, fmin: efMin, fmax: efMax, W, H };
}

// ─── SVG Export ───
// Font names use single quotes so the string can safely sit inside a double-quoted SVG attribute.
const CM_FONT_FAMILY = `'Computer Modern Serif','CMU Serif','Latin Modern Roman','Times New Roman',serif`;

// Escape characters with special meaning in XML/SVG text and attributes.
function escapeXML(s) {
  return String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&apos;"}[c]));
}
// Escape characters with special meaning inside PostScript / PDF literal strings: (, ), \
function escapePSString(s) {
  return String(s).replace(/([\\()])/g, "\\$1");
}
function exportSVG(scene) {
  const {faces, visibleEdges, contours, lp, fieldName, fmin, fmax, W, H} = scene;
  const lines = [];
  lines.push(`<?xml version="1.0" encoding="UTF-8"?>`);
  lines.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">`);
  lines.push(`<rect width="${W}" height="${H}" fill="white"/>`);

  // Faces (fill only, no stroke)
  for (const f of faces) {
    const pts = f.screenPts.map(p => `${p[0].toFixed(2)},${p[1].toFixed(2)}`).join(" ");
    const col = `rgb(${(f.r*255)|0},${(f.g*255)|0},${(f.b*255)|0})`;
    lines.push(`<polygon points="${pts}" fill="${col}" stroke="none"/>`);
  }

  // Visible wireframe edges (hidden-line removed)
  if (visibleEdges.length > 0) {
    lines.push(`<g stroke="#222" stroke-width="${lp.svgW}" stroke-linecap="round" fill="none">`);
    for (const [a,b] of visibleEdges) {
      lines.push(`<line x1="${a[0].toFixed(2)}" y1="${a[1].toFixed(2)}" x2="${b[0].toFixed(2)}" y2="${b[1].toFixed(2)}"/>`);
    }
    lines.push(`</g>`);
  }

  // Contour lines — thick, colored by iso-level
  if (contours.length > 0) {
    for (const c of contours) {
      const col=`rgb(${(c.r*255)|0},${(c.g*255)|0},${(c.b*255)|0})`;
      lines.push(`<line x1="${c.p1[0].toFixed(2)}" y1="${c.p1[1].toFixed(2)}" x2="${c.p2[0].toFixed(2)}" y2="${c.p2[1].toFixed(2)}" stroke="${col}" stroke-width="1" stroke-linecap="round"/>`);
    }
  }

  // Vertical color bar on right with 6 labels
  if (fieldName) {
    const bx = W - 40, by = H/2 - 110, bw = 18, bh = 220, nSteps = 64, nLabels = 6;
    for (let i = 0; i < nSteps; i++) {
      const t = i/(nSteps-1), [r,g,b] = sampleTurbo(t);
      const ry = by + bh - (i+1)*bh/nSteps;
      lines.push(`<rect x="${bx}" y="${ry.toFixed(1)}" width="${bw}" height="${(bh/nSteps+0.5).toFixed(1)}" fill="rgb(${(r*255)|0},${(g*255)|0},${(b*255)|0})"/>`);
    }
    lines.push(`<rect x="${bx-0.5}" y="${by-0.5}" width="${bw+1}" height="${bh+1}" fill="none" stroke="#555" stroke-width="0.5"/>`);
    for (let i = 0; i < nLabels; i++) {
      const t = i/(nLabels-1);
      const v = fmax - t*(fmax-fmin);
      const ly = by + t*bh + 4.5;
      lines.push(`<text x="${bx-5}" y="${ly.toFixed(1)}" font-family="${CM_FONT_FAMILY}" font-size="12.5" font-weight="bold" fill="#333" text-anchor="end">${escapeXML(v.toExponential(2))}</text>`);
    }
    lines.push(`<text x="${bx+bw+16}" y="${by+bh/2}" font-family="${CM_FONT_FAMILY}" font-size="14" font-weight="bold" fill="#333" text-anchor="middle" transform="rotate(90,${bx+bw+16},${by+bh/2})">${escapeXML(fieldName)}</text>`);
  }

  lines.push(`</svg>`);
  return lines.join("\n");
}

// ─── EPS Export ───
function exportEPS(scene) {
  const {faces, visibleEdges, contours, lp, fieldName, fmin, fmax, W, H} = scene;
  const ps = [];
  ps.push(`%!PS-Adobe-3.0 EPSF-3.0`);
  ps.push(`%%BoundingBox: 0 0 ${W} ${H}`);
  ps.push(`%%Title: FE Export`);
  ps.push(`%%Creator: SIMPLAS Viewer`);
  ps.push(`%%EndComments`);
  ps.push(`/CMFont { /CMB10 findfont } stopped { /Times-Bold findfont } if def`);
  ps.push(`CMFont 12.5 scalefont setfont`);
  ps.push(`1 1 1 setrgbcolor 0 0 ${W} ${H} rectfill`);

  // Faces (fill only)
  for (const f of faces) {
    ps.push(`${f.r.toFixed(4)} ${f.g.toFixed(4)} ${f.b.toFixed(4)} setrgbcolor`);
    ps.push(`newpath`);
    f.screenPts.forEach((p,i) => {
      const py = H - p[1];
      ps.push(`${p[0].toFixed(2)} ${py.toFixed(2)} ${i===0?"moveto":"lineto"}`);
    });
    ps.push(`closepath fill`);
  }

  // Visible edges (hidden-line removed)
  if (visibleEdges.length > 0) {
    ps.push(`0.13 0.13 0.13 setrgbcolor ${lp.svgW} setlinewidth 1 setlinecap`);
    for (const [a,b] of visibleEdges) {
      ps.push(`newpath ${a[0].toFixed(2)} ${(H-a[1]).toFixed(2)} moveto ${b[0].toFixed(2)} ${(H-b[1]).toFixed(2)} lineto stroke`);
    }
  }

  // Contour lines — thick, colored by iso-level
  if (contours.length > 0) {
    ps.push(`1 setlinewidth 1 setlinecap`);
    for (const c of contours) {
      ps.push(`${c.r.toFixed(4)} ${c.g.toFixed(4)} ${c.b.toFixed(4)} setrgbcolor`);
      ps.push(`newpath ${c.p1[0].toFixed(2)} ${(H-c.p1[1]).toFixed(2)} moveto ${c.p2[0].toFixed(2)} ${(H-c.p2[1]).toFixed(2)} lineto stroke`);
    }
  }

  // Vertical color bar on right, 6 labels
  if (fieldName) {
    const bx = W - 40, byBot = 24, bh = 220, bw = 18, nSteps = 64, nLabels = 6;
    const byTop = byBot + bh;
    for (let i = 0; i < nSteps; i++) {
      const t = i/(nSteps-1), [r,g,b] = sampleTurbo(t);
      const ry = byBot + i * bh / nSteps;
      ps.push(`${r.toFixed(4)} ${g.toFixed(4)} ${b.toFixed(4)} setrgbcolor`);
      ps.push(`${bx} ${ry.toFixed(2)} ${bw} ${(bh/nSteps+0.5).toFixed(2)} rectfill`);
    }
    ps.push(`0.33 0.33 0.33 setrgbcolor 0.5 setlinewidth`);
    ps.push(`${bx-0.5} ${byBot-0.5} ${bw+1} ${bh+1} rectstroke`);
    ps.push(`0.2 0.2 0.2 setrgbcolor`);
    ps.push(`CMFont 12.5 scalefont setfont`);
    for (let i = 0; i < nLabels; i++) {
      const t = i/(nLabels-1);
      const v = fmin + t*(fmax-fmin);
      const ly = byBot + t*bh - 4;
      ps.push(`${bx-5} ${ly.toFixed(2)} moveto (${escapePSString(v.toExponential(2))}) dup stringwidth pop neg 0 rmoveto show`);
    }
    ps.push(`CMFont 14 scalefont setfont`);
    ps.push(`gsave ${bx+bw+16} ${byBot+bh/2} translate 90 rotate`);
    ps.push(`(${escapePSString(fieldName)}) dup stringwidth pop 2 div neg 0 moveto show`);
    ps.push(`grestore`);
  }

  ps.push(`%%EOF`);
  return ps.join("\n");
}

// ─── PDF Export (vector, minimal valid PDF) ───
function exportPDF(scene) {
  const {faces, visibleEdges, contours, lp, fieldName, fmin, fmax, W, H} = scene;
  const stream = [];

  stream.push(`1 1 1 rg 0 0 ${W} ${H} re f`);

  // Faces (fill only, painter's algorithm)
  for (const f of faces) {
    stream.push(`${f.r.toFixed(4)} ${f.g.toFixed(4)} ${f.b.toFixed(4)} rg`);
    f.screenPts.forEach((p,i) => {
      const py = H - p[1];
      stream.push(`${p[0].toFixed(2)} ${py.toFixed(2)} ${i===0?"m":"l"}`);
    });
    stream.push(`h f`);
  }

  // Visible edges
  if (visibleEdges.length > 0) {
    stream.push(`0.13 0.13 0.13 RG ${lp.svgW} w 1 j 1 J`);
    for (const [a,b] of visibleEdges) {
      stream.push(`${a[0].toFixed(2)} ${(H-a[1]).toFixed(2)} m ${b[0].toFixed(2)} ${(H-b[1]).toFixed(2)} l S`);
    }
  }

  // Contour lines — thick, colored by iso-level
  if (contours.length > 0) {
    stream.push(`1 w 1 J`);
    for (const c of contours) {
      stream.push(`${c.r.toFixed(4)} ${c.g.toFixed(4)} ${c.b.toFixed(4)} RG`);
      stream.push(`${c.p1[0].toFixed(2)} ${(H-c.p1[1]).toFixed(2)} m ${c.p2[0].toFixed(2)} ${(H-c.p2[1]).toFixed(2)} l S`);
    }
  }

  // Vertical color bar on right, 6 labels
  if (fieldName) {
    const bx = W - 40, byBot = 24, bh = 220, bw = 18, nSteps = 64, nLabels = 6;
    for (let i = 0; i < nSteps; i++) {
      const t = i/(nSteps-1), [r,g,b] = sampleTurbo(t);
      const ry = byBot + i * bh / nSteps;
      stream.push(`${r.toFixed(4)} ${g.toFixed(4)} ${b.toFixed(4)} rg`);
      stream.push(`${bx} ${ry.toFixed(2)} ${bw} ${(bh/nSteps+0.5).toFixed(2)} re f`);
    }
    stream.push(`0.33 0.33 0.33 RG 0 0 0 rg 0.5 w`);
    stream.push(`${bx-0.5} ${byBot-0.5} ${bw+1} ${bh+1} re S`);
    stream.push(`0.2 0.2 0.2 rg`);
    for (let i = 0; i < nLabels; i++) {
      const t = i/(nLabels-1);
      const v = fmin + t*(fmax-fmin);
      const ly = byBot + t*bh - 4;
      stream.push(`BT /F1 12.5 Tf ${bx-5} ${ly.toFixed(2)} Td (${escapePSString(v.toExponential(2))}) Tj ET`);
    }
    // Text matrix for a 90° counter-clockwise rotation: [cos θ, sin θ, -sin θ, cos θ, tx, ty].
    // Font scaling is applied via Tf, not by inflating the matrix.
    stream.push(`BT /F1 14 Tf 0 1 -1 0 ${bx+bw+16} ${byBot+bh/4} Tm (${escapePSString(fieldName)}) Tj ET`);
  }

  const content = stream.join("\n");
  const contentBytes = new TextEncoder().encode(content);

  // Build minimal PDF
  const objs = [];
  // Obj 1: Catalog
  objs.push(`1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj`);
  // Obj 2: Pages
  objs.push(`2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj`);
  // Obj 3: Page
  objs.push(`3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${W} ${H}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj`);
  // Obj 4: Content stream
  objs.push(`4 0 obj\n<< /Length ${contentBytes.length} >>\nstream\n${content}\nendstream\nendobj`);
  // Obj 5: Font
  objs.push(`5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold >>\nendobj`);

  let pdf = `%PDF-1.4\n`;
  const offsets = [];
  for (let i = 0; i < objs.length; i++) {
    offsets.push(pdf.length);
    pdf += objs[i] + "\n";
  }
  const xrefOff = pdf.length;
  pdf += `xref\n0 ${objs.length + 1}\n`;
  pdf += `0000000000 65535 f \n`;
  for (const off of offsets) {
    pdf += `${String(off).padStart(10, "0")} 00000 n \n`;
  }
  pdf += `trailer\n<< /Size ${objs.length + 1} /Root 1 0 R >>\n`;
  pdf += `startxref\n${xrefOff}\n%%EOF`;
  return pdf;
}

function downloadBlob(content, filename, mime) {
  const blob = new Blob([content], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url; a.download = filename;
  document.body.appendChild(a); a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

// ─── Component ───
export default function FEPostprocessor() {
  const canvasRef=useRef(null), sceneRef=useRef({}), animRef=useRef(null);
  const mouseRef=useRef({down:false,button:0,x:0,y:0});
  const camRef=useRef({theta:0.6,phi:0.8,dist:3.5,tx:0,ty:0});
  const touchRef=useRef({active:false,x:0,y:0,dist:0,count:0});
  const bboxRef=useRef({mn:[-1,-1,-1],mx:[1,1,1]}); // normalized mesh bbox
  const needsFitRef=useRef(true); // auto zoom-to-fit on first load

  const [meshData,setMeshData]=useState(null);
  const [activeDemo,setActiveDemo]=useState(-1);
  const [activeFinerDemo,setActiveFinerDemo]=useState(-1);
  const [activeField,setActiveField]=useState("");

  const [displayMode,setDisplayMode]=useState("plot"); // "wireframe"|"plot"|"lines"
  const [showDef,setShowDef]=useState(false);
  const [defScale,setDefScale]=useState(1);
  const [contourN,setContourN]=useState(10);
  // Draft value shown while dragging the iso-level slider; contourN only updates on release
  // to avoid rebuilding the whole Three.js scene on every slider tick.
  const [contourDraft,setContourDraft]=useState(10);
  useEffect(()=>{setContourDraft(contourN);},[contourN]);
  const [fRange,setFRange]=useState([0,1]);
  const [userMin,setUserMin]=useState(""); // "" = auto
  const [userMax,setUserMax]=useState(""); // "" = auto
  const [info,setInfo]=useState("");
  const [log,setLog]=useState("");
  const [savedViews,setSavedViews]=useState([]);
  const [viewName,setViewName]=useState("");

  // Load saved views from persistent storage on mount
  useEffect(()=>{
    (async()=>{
      try {
        const r = await window.storage.get("fe-saved-views");
        if (r && r.value) setSavedViews(JSON.parse(r.value));
      } catch(e) { /* no saved views yet */ }
    })();
  },[]);

  const persistViews = useCallback(async (views) => {
    setSavedViews(views);
    try { await window.storage.set("fe-saved-views", JSON.stringify(views)); } catch(e) {}
  },[]);

  const saveCurrentView = useCallback(()=>{
    const cam = {...camRef.current};
    const name = viewName.trim() || `View ${savedViews.length+1}`;
    const entry = { name, cam, ts: Date.now() };
    const updated = [entry, ...savedViews].slice(0, 10); // keep last 10, newest first
    persistViews(updated);
    setViewName("");
  },[savedViews, viewName, persistViews]);

  const deleteView = useCallback((idx)=>{
    const updated = savedViews.filter((_,i)=>i!==idx);
    persistViews(updated);
  },[savedViews, persistViews]);

  const restoreView = useCallback((v)=>{
    camRef.current = {...v.cam};
  },[]);

  const demos=useMemo(()=>[
    {name:"Plate with Hole (2D Quads)", make:()=>genPlateHole()},
    {name:"Cantilever (3D Hex8)",       make:()=>gen3DBeam()},
    {name:"Unit Square (2D Tri3)",      make:()=>gen2DTri()},
  ],[]);

  const finerDemos=useMemo(()=>[
    {name:"Plate with Hole — Fine (2D Quads)",       make:()=>genPlateHole({nr:48, nth:128, no:48, name:"Plate with Hole — Fine (2D Quads)"})},
    {name:"Plate with Hole — Ultra-Fine (2D Quads)", make:()=>genPlateHole({nr:96, nth:256, no:96, name:"Plate with Hole — Ultra-Fine (2D Quads)"})},
    {name:"Cantilever — Fine (3D Hex8)",             make:()=>gen3DBeam({nx:96, ny:20, nz:20, name:"Cantilever — Fine (3D Hex8)"})},
    {name:"Cantilever — Ultra-Fine (3D Hex8)",       make:()=>gen3DBeam({nx:160, ny:32, nz:32, name:"Cantilever — Ultra-Fine (3D Hex8)"})},
    {name:"Unit Square — Fine (2D Tri3)",            make:()=>gen2DTri({n:80, name:"Unit Square — Fine (2D Tri3)"})},
    {name:"Unit Square — Ultra-Fine (2D Tri3)",      make:()=>gen2DTri({n:160, name:"Unit Square — Ultra-Fine (2D Tri3)"})},
  ],[]);

  const loadMesh=useCallback((d)=>{
    setMeshData(d);
    const sf=Object.keys(d.fields||{}).filter(f=>d.fields[f].type==="scalar");
    setActiveField(sf[0]||"");
    camRef.current=d.dim===2?{theta:0,phi:Math.PI/2,dist:1.5,tx:0,ty:0}:{theta:0.6,phi:0.8,dist:2.0,tx:0,ty:0};
    needsFitRef.current=true; // trigger auto zoom-to-fit after bbox is computed
    setInfo(`${d.name} — ${d.nodes.length} nodes, ${d.elements.length} elems`);
  },[]);

  useEffect(()=>{if(activeDemo>=0)loadMesh(demos[activeDemo].make());},[activeDemo,demos,loadMesh]);
  useEffect(()=>{if(activeFinerDemo>=0)loadMesh(finerDemos[activeFinerDemo].make());},[activeFinerDemo,finerDemos,loadMesh]);

  // Boundary-face extraction and dimensionality only depend on mesh topology, so they can
  // be cached across unrelated state changes (active field, display mode, slider moves...).
  const meshTopo=useMemo(()=>{
    if(!meshData) return null;
    const is3D=meshData.dim===3||meshData.elements.some(e=>{const ft=FACE_TABLE[e.type];return ft&&ft.dim===3;});
    const bfaces=extractBoundaryFaces(meshData.elements,is3D);
    return {is3D,bfaces};
  },[meshData]);

  // ─── Three.js Scene ───
  useEffect(()=>{
    if(!canvasRef.current||!meshData||!meshTopo) return;
    const canvas=canvasRef.current, w=canvas.clientWidth, h=canvas.clientHeight;
    let ren=sceneRef.current.renderer;
    if(!ren){ren=new THREE.WebGLRenderer({canvas,antialias:true});sceneRef.current.renderer=ren;}
    ren.setSize(w,h); ren.setPixelRatio(Math.min(window.devicePixelRatio,2)); ren.setClearColor(0xffffff);

    const asp=w/h;
    const scene=new THREE.Scene();
    const cam=new THREE.OrthographicCamera(-1*asp,1*asp,1,-1,0.01,100);
    sceneRef.current.scene=scene; sceneRef.current.camera=cam;
    // Subtle fixed lighting
    scene.add(new THREE.AmbientLight(0xffffff, 0.88));
    const dl=new THREE.DirectionalLight(0xffffff, 0.12); dl.position.set(0.4,0.7,0.5); scene.add(dl);

    const ns=meshData.nodes;
    let mn=[1e9,1e9,1e9],mx=[-1e9,-1e9,-1e9];
    for(const n of ns) for(let d=0;d<3;d++){mn[d]=Math.min(mn[d],n[d]);mx[d]=Math.max(mx[d],n[d]);}
    const cen=[(mn[0]+mx[0])/2,(mn[1]+mx[1])/2,(mn[2]+mx[2])/2];
    const span=Math.max(mx[0]-mn[0],mx[1]-mn[1],mx[2]-mn[2],1e-12), sc=2/span;
    // Store normalized bbox for zoom-to-fit
    bboxRef.current={mn:[(mn[0]-cen[0])*sc,(mn[1]-cen[1])*sc,(mn[2]-cen[2])*sc],
                     mx:[(mx[0]-cen[0])*sc,(mx[1]-cen[1])*sc,(mx[2]-cen[2])*sc]};

    // Auto zoom-to-fit on first render after mesh load
    if(needsFitRef.current && canvas.clientWidth>0){
      needsFitRef.current=false;
      const c=camRef.current, asp=canvas.clientWidth/canvas.clientHeight;
      const bb=bboxRef.current;
      const sphi=Math.sin(c.phi),cphi=Math.cos(c.phi),sth=Math.sin(c.theta),cth=Math.cos(c.theta);
      let fwdV=[-sphi*sth,-cphi,-sphi*cth], upV=[0,1,0];
      if(Math.abs(fwdV[0]*upV[0]+fwdV[1]*upV[1]+fwdV[2]*upV[2])>0.99) upV=[0,0,-1];
      const rl2=Math.sqrt((fwdV[1]*upV[2]-fwdV[2]*upV[1])**2+(fwdV[2]*upV[0]-fwdV[0]*upV[2])**2+(fwdV[0]*upV[1]-fwdV[1]*upV[0])**2);
      const rv=[(fwdV[1]*upV[2]-fwdV[2]*upV[1])/rl2,(fwdV[2]*upV[0]-fwdV[0]*upV[2])/rl2,(fwdV[0]*upV[1]-fwdV[1]*upV[0])/rl2];
      const uv=[rv[1]*fwdV[2]-rv[2]*fwdV[1],rv[2]*fwdV[0]-rv[0]*fwdV[2],rv[0]*fwdV[1]-rv[1]*fwdV[0]];
      let x0=1e9,x1=-1e9,y0=1e9,y1=-1e9;
      for(let ix=0;ix<2;ix++)for(let iy=0;iy<2;iy++)for(let iz=0;iz<2;iz++){
        const px=ix?bb.mx[0]:bb.mn[0],py=iy?bb.mx[1]:bb.mn[1],pz=iz?bb.mx[2]:bb.mn[2];
        const vx=px*rv[0]+py*rv[1]+pz*rv[2], vy=px*uv[0]+py*uv[1]+pz*uv[2];
        x0=Math.min(x0,vx);x1=Math.max(x1,vx);y0=Math.min(y0,vy);y1=Math.max(y1,vy);
      }
      const vW=x1-x0, vH=y1-y0, vCx=(x0+x1)/2, vCy=(y0+y1)/2;
      const hasField=!!activeField;
      const cbF=hasField?1.25:1.0;
      c.dist=Math.max(vH/2*1.08, vW/2*1.08*cbF/asp, 0.3);
      c.tx=hasField?vCx-c.dist*asp*0.1:vCx;
      c.ty=vCy;
    }

    const {is3D,bfaces}=meshTopo;

    const dispF=meshData.fields?.Displacement||meshData.fields?.displacement;
    const dp=ns.map((n,i)=>{
      const d=(showDef&&dispF?.type==="vector")?dispF.values[i]:[0,0,0];
      return [(n[0]+d[0]*defScale-cen[0])*sc,(n[1]+d[1]*defScale-cen[1])*sc,(n[2]+d[2]*defScale-cen[2])*sc];
    });

    const field=meshData.fields?.[activeField];
    let fmin=0,fmax=1,fv=null;
    if(field?.type==="scalar"){fv=field.values;fmin=Infinity;fmax=-Infinity;for(const v of fv){if(v<fmin)fmin=v;if(v>fmax)fmax=v;}
      if(Math.abs(fmax-fmin)<1e-15)fmax=fmin+1; setFRange([fmin,fmax]);}
    // Effective range: user overrides or auto
    const eMin = userMin!==""&&!isNaN(parseFloat(userMin)) ? parseFloat(userMin) : fmin;
    const eMax = userMax!==""&&!isNaN(parseFloat(userMax)) ? parseFloat(userMax) : fmax;
    const eSpan = Math.abs(eMax-eMin)<1e-15 ? 1 : eMax-eMin;

    const pos=[],col=[],wpos=[];

    for(const face of bfaces){
      for(const tri of triangulateFace(face)){
        for(const ni of tri){
          const p=dp[ni]; pos.push(p[0],p[1],p[2]);
          // In "lines" mode: white fill (contour lines carry color); otherwise field colors
          if(displayMode==="lines"){
            col.push(1.0,1.0,1.0);
          } else if(fv){
            const t=(fv[ni]-eMin)/eSpan;const[r,g,b]=sampleTurbo(t);col.push(r,g,b);
          } else col.push(0.75,0.78,0.82);
        }
      }
      for(let j=0;j<face.length;j++){
        const a=dp[face[j]],b=dp[face[(j+1)%face.length]];
        wpos.push(a[0],a[1],a[2],b[0],b[1],b[2]);
      }
    }

    // ── Build mesh geometry ──
    const meshGeom=new THREE.BufferGeometry();
    meshGeom.setAttribute("position",new THREE.Float32BufferAttribute(pos,3));
    meshGeom.setAttribute("color",new THREE.Float32BufferAttribute(col,3));
    meshGeom.computeVertexNormals();

    // Depth-only pre-pass for hidden-line removal
    scene.add(new THREE.Mesh(meshGeom, new THREE.MeshBasicMaterial({
      colorWrite:false, depthWrite:true, side:THREE.DoubleSide,
      polygonOffset:true, polygonOffsetFactor:1, polygonOffsetUnits:1,
    })));

    const showFill = displayMode==="plot"||displayMode==="lines";
    const showAllEdges = displayMode==="wireframe"||displayMode==="plot";
    const showIsoLines = displayMode==="lines";
    const showFeatureOnly = displayMode==="lines";

    // Visible fill
    if(showFill){
      scene.add(new THREE.Mesh(meshGeom, new THREE.MeshLambertMaterial({
        vertexColors:true, side:THREE.DoubleSide,
      })));
    }

    // Edges: all element edges or feature edges only
    if(showAllEdges){
      const lp=autoLineWeight(wpos.length/6);
      const wGeom=new THREE.BufferGeometry();
      wGeom.setAttribute("position",new THREE.Float32BufferAttribute(wpos,3));
      scene.add(new THREE.LineSegments(wGeom, new THREE.LineBasicMaterial({
        color:0x222222, transparent:true, opacity:lp.opacity, depthTest:true,
      })));
    } else if(showFeatureOnly){
      const featPos=extractFeatureEdges(bfaces,dp,20);
      if(featPos.length>0){
        const fGeom=new THREE.BufferGeometry();
        fGeom.setAttribute("position",new THREE.Float32BufferAttribute(featPos,3));
        scene.add(new THREE.LineSegments(fGeom, new THREE.LineBasicMaterial({
          color:0x333333, transparent:true, opacity:0.7, depthTest:true,
        })));
      }
    }

    // Contour iso-lines — smoothed, 1pt, colored by level
    if(showIsoLines&&fv){
      const rawSegs=computeContourSegments(bfaces,dp,fv,eMin,eMax,contourN);
      const csegs=smoothContours(rawSegs,2);
      if(csegs.length>0){
        const cp=[],cc=[];
        for(const [a,b,lv] of csegs){
          cp.push(a[0],a[1],a[2],b[0],b[1],b[2]);
          const t=(lv-eMin)/eSpan;
          const [cr,cg,cb]=sampleTurbo(t);
          cc.push(cr,cg,cb,cr,cg,cb);
        }
        const cGeom=new THREE.BufferGeometry();
        cGeom.setAttribute("position",new THREE.Float32BufferAttribute(cp,3));
        cGeom.setAttribute("color",new THREE.Float32BufferAttribute(cc,3));
        // Query GPU for max supported line width
        const gl=ren.getContext();
        const maxLW=Math.min(gl.getParameter(gl.ALIASED_LINE_WIDTH_RANGE)[1]||1, 1);
        scene.add(new THREE.LineSegments(cGeom, new THREE.LineBasicMaterial({
          vertexColors:true, linewidth:maxLW, depthTest:true,
        })));
      }
    }

    // 3D triad — separate scene, fixed corner position
    let triadScene=null, triadCam=null;
    if(is3D){
      triadScene=new THREE.Scene();
      triadCam=new THREE.OrthographicCamera(-1.2,1.2,1.2,-1.2,0.01,50);
      triadScene.add(new THREE.AmbientLight(0xffffff,1));
      const tL=0.72, sR=0.045, hR=0.11, hL=0.2;
      const cols=[0xdd2222,0x22aa22,0x2255dd];
      const dirs=[[1,0,0],[0,1,0],[0,0,1]];
      const labels=["X\u2081","X\u2082","X\u2083"];
      for(let i=0;i<3;i++){
        const mat=new THREE.MeshBasicMaterial({color:cols[i]});
        const shaft=new THREE.Mesh(new THREE.CylinderGeometry(sR,sR,tL-hL,10),mat);
        shaft.position.y=(tL-hL)/2;
        const head=new THREE.Mesh(new THREE.ConeGeometry(hR,hL,10),mat);
        head.position.y=tL-hL/2;
        const g=new THREE.Group(); g.add(shaft); g.add(head);
        if(dirs[i][0]===1) g.rotation.z=-Math.PI/2;
        else if(dirs[i][2]===1) g.rotation.x=Math.PI/2;
        triadScene.add(g);
        const cv=document.createElement("canvas"); cv.width=128; cv.height=64;
        const ctx=cv.getContext("2d");
        ctx.font="bold italic 42px 'Computer Modern Serif',serif";
        ctx.fillStyle=["#dd2222","#22aa22","#2255dd"][i];
        ctx.textAlign="center"; ctx.textBaseline="middle";
        ctx.fillText(labels[i],64,32);
        const tex=new THREE.CanvasTexture(cv);
        const sp=new THREE.Sprite(new THREE.SpriteMaterial({map:tex,transparent:true,depthTest:false}));
        sp.scale.set(0.42,0.21,1);
        const tipOff=tL+0.08;
        sp.position.set(dirs[i][0]?tipOff:0, dirs[i][1]?tipOff:0, dirs[i][2]?tipOff:0);
        triadScene.add(sp);
      }
    }

    const animate=()=>{
      const c=camRef.current;
      // Apply momentum when not dragging
      if(!mouseRef.current.down&&!touchRef.current.active){
        const v=velRef.current;
        if(Math.abs(v.vt)>1e-5||Math.abs(v.vp)>1e-5){
          c.theta+=v.vt; c.phi=Math.max(.01,Math.min(Math.PI-.01,c.phi+v.vp));
          v.vt*=0.92; v.vp*=0.92; // exponential decay
        }
      }
      const cw=canvas.clientWidth, ch=canvas.clientHeight, a=cw/ch;
      // Main scene
      cam.left=-c.dist*a; cam.right=c.dist*a; cam.top=c.dist; cam.bottom=-c.dist;
      cam.updateProjectionMatrix();
      const fd=20;
      cam.position.set(fd*Math.sin(c.phi)*Math.sin(c.theta)+c.tx, fd*Math.cos(c.phi)+c.ty, fd*Math.sin(c.phi)*Math.cos(c.theta));
      cam.lookAt(c.tx,c.ty,0); cam.up.set(0,1,0);
      ren.setViewport(0,0,cw,ch);
      ren.setScissor(0,0,cw,ch);
      ren.setScissorTest(true);
      ren.render(scene,cam);
      // Triad in bottom-left corner
      if(triadScene&&triadCam){
        const ts=Math.min(cw,ch)*0.25|0;
        ren.setViewport(4,4,ts,ts);
        ren.setScissor(4,4,ts,ts);
        ren.clearDepth();
        triadCam.position.set(
          10*Math.sin(c.phi)*Math.sin(c.theta),
          10*Math.cos(c.phi),
          10*Math.sin(c.phi)*Math.cos(c.theta)
        );
        triadCam.lookAt(0,0,0); triadCam.up.set(0,1,0);
        ren.render(triadScene,triadCam);
      }
      ren.setScissorTest(false);
      animRef.current=requestAnimationFrame(animate);
    };
    animate();
    return()=>{cancelAnimationFrame(animRef.current);
      // Dispose geometries, materials, and any textures bound to them (e.g. triad sprite labels).
      const disposeObj=o=>{
        o.geometry?.dispose();
        if(o.material){
          const mats=Array.isArray(o.material)?o.material:[o.material];
          for(const m of mats){ m.map?.dispose?.(); m.dispose(); }
        }
      };
      scene.traverse(disposeObj);
      if(triadScene) triadScene.traverse(disposeObj);
    };
  },[meshData,meshTopo,activeField,displayMode,showDef,defScale,contourN,userMin,userMax]);

  // Dispose the WebGLRenderer and free the WebGL context when the component unmounts.
  useEffect(()=>()=>{
    const r=sceneRef.current.renderer;
    if(r){ r.dispose(); r.forceContextLoss?.(); sceneRef.current.renderer=null; }
  },[]);

  useEffect(()=>{
    const fn=()=>{const c=canvasRef.current;if(!c||!sceneRef.current.renderer)return;
      sceneRef.current.renderer.setSize(c.clientWidth,c.clientHeight);
      if(sceneRef.current.camera) sceneRef.current.camera.updateProjectionMatrix();};
    window.addEventListener("resize",fn); return()=>window.removeEventListener("resize",fn);
  },[]);

  // Orbit controls with pole compensation and momentum.
  // Handlers only read from refs, so they can be created once and live for the component lifetime.
  const velRef=useRef({vt:0,vp:0}); // angular velocity for momentum
  const onMD=useCallback(e=>{mouseRef.current={down:true,button:e.button,x:e.clientX,y:e.clientY};velRef.current.vt=0;velRef.current.vp=0;e.preventDefault();},[]);
  const onMM=useCallback(e=>{if(!mouseRef.current.down)return;const dx=e.clientX-mouseRef.current.x,dy=e.clientY-mouseRef.current.y;
    if(mouseRef.current.button===0){
      // Compensate horizontal speed near poles (divide by sin(phi))
      const sinP=Math.max(0.15,Math.abs(Math.sin(camRef.current.phi)));
      const dt=-dx*.005/sinP, dp=-dy*.005;
      camRef.current.theta+=dt;
      camRef.current.phi=Math.max(.01,Math.min(Math.PI-.01,camRef.current.phi+dp));
      velRef.current.vt=dt; velRef.current.vp=dp;
    } else if(mouseRef.current.button===2){
      camRef.current.tx+=dx*.003*camRef.current.dist;
      camRef.current.ty-=dy*.003*camRef.current.dist;
    }
    mouseRef.current.x=e.clientX;mouseRef.current.y=e.clientY;},[]);
  const onMU=useCallback(()=>{mouseRef.current.down=false;},[]);
  const onWH=useCallback(e=>{camRef.current.dist*=e.deltaY>0?1.08:.92;camRef.current.dist=Math.max(.3,Math.min(20,camRef.current.dist));},[]);
  // Touch with same pole compensation
  const onTS=useCallback(e=>{velRef.current.vt=0;velRef.current.vp=0;
    if(e.touches.length===1)touchRef.current={active:true,x:e.touches[0].clientX,y:e.touches[0].clientY,dist:0,count:1};
    else if(e.touches.length===2){const dx=e.touches[1].clientX-e.touches[0].clientX,dy=e.touches[1].clientY-e.touches[0].clientY;
      touchRef.current={active:true,x:(e.touches[0].clientX+e.touches[1].clientX)/2,y:(e.touches[0].clientY+e.touches[1].clientY)/2,dist:Math.sqrt(dx*dx+dy*dy),count:2};}},[]);
  const onTM=useCallback(e=>{e.preventDefault();const t=touchRef.current;if(!t.active)return;
    if(t.count===1&&e.touches.length===1){
      const dx=e.touches[0].clientX-t.x, dy=e.touches[0].clientY-t.y;
      const sinP=Math.max(0.15,Math.abs(Math.sin(camRef.current.phi)));
      camRef.current.theta-=dx*.005/sinP;
      camRef.current.phi=Math.max(.01,Math.min(Math.PI-.01,camRef.current.phi-dy*.005));
      t.x=e.touches[0].clientX;t.y=e.touches[0].clientY;
    } else if(t.count===2&&e.touches.length===2){const dx=e.touches[1].clientX-e.touches[0].clientX,dy=e.touches[1].clientY-e.touches[0].clientY,d=Math.sqrt(dx*dx+dy*dy);if(t.dist>0)camRef.current.dist*=t.dist/d;camRef.current.dist=Math.max(.3,Math.min(20,camRef.current.dist));t.dist=d;}},[]);
  const onTE=useCallback(()=>{touchRef.current.active=false;},[]);

  // Zoom to fit: project bbox through current view, compute optimal frustum
  const zoomToFit=useCallback(()=>{
    const c=camRef.current;
    const canvas=canvasRef.current;
    if(!canvas) return;
    const aspect=canvas.clientWidth/canvas.clientHeight;
    const bb=bboxRef.current;

    // Build view vectors from current theta/phi
    const sphi=Math.sin(c.phi),cphi=Math.cos(c.phi),sth=Math.sin(c.theta),cth=Math.cos(c.theta);
    let fwd=[-sphi*sth,-cphi,-sphi*cth];
    let up=[0,1,0];
    if(Math.abs(fwd[0]*up[0]+fwd[1]*up[1]+fwd[2]*up[2])>0.99) up=[0,0,-1];
    const rl=Math.sqrt((fwd[1]*up[2]-fwd[2]*up[1])**2+(fwd[2]*up[0]-fwd[0]*up[2])**2+(fwd[0]*up[1]-fwd[1]*up[0])**2);
    const right=[(fwd[1]*up[2]-fwd[2]*up[1])/rl,(fwd[2]*up[0]-fwd[0]*up[2])/rl,(fwd[0]*up[1]-fwd[1]*up[0])/rl];
    const upC=[right[1]*fwd[2]-right[2]*fwd[1],right[2]*fwd[0]-right[0]*fwd[2],right[0]*fwd[1]-right[1]*fwd[0]];

    // Project 8 bbox corners onto view plane
    let xmn=1e9,xmx=-1e9,ymn=1e9,ymx=-1e9;
    for(let ix=0;ix<2;ix++) for(let iy=0;iy<2;iy++) for(let iz=0;iz<2;iz++){
      const px=ix?bb.mx[0]:bb.mn[0], py=iy?bb.mx[1]:bb.mn[1], pz=iz?bb.mx[2]:bb.mn[2];
      const vx=px*right[0]+py*right[1]+pz*right[2];
      const vy=px*upC[0]+py*upC[1]+pz*upC[2];
      xmn=Math.min(xmn,vx); xmx=Math.max(xmx,vx);
      ymn=Math.min(ymn,vy); ymx=Math.max(ymx,vy);
    }

    const viewW=xmx-xmn, viewH=ymx-ymn;
    const viewCx=(xmn+xmx)/2, viewCy=(ymn+ymx)/2;

    // Pad and reserve space for color bar on right (~20% of width)
    const pad=1.08;
    const cbExtra=activeField?1.25:1.0;
    const needHalfW=viewW/2*pad*cbExtra;
    const needHalfH=viewH/2*pad;
    const dist=Math.max(needHalfH, needHalfW/aspect, 0.5);

    c.dist=dist;
    // Offset center: shift left by half the color bar space
    c.tx=activeField ? viewCx-dist*aspect*0.1 : viewCx;
    c.ty=viewCy;
    // Kill momentum
    velRef.current.vt=0; velRef.current.vp=0;
  },[activeField]);

  // ─── Ensight Loader ───
  const handleEnsight=useCallback(async e=>{
    const files=Array.from(e.target.files||[]); if(!files.length)return;
    setLog("Reading files...");
    const readF=f=>new Promise((res,rej)=>{const r=new FileReader();r.onload=ev=>res(ev.target.result);r.onerror=rej;r.readAsText(f);});
    const fm={};
    for(const f of files) fm[f.name]=await readF(f);

    try {
    const caseF=files.find(f=>f.name.endsWith(".case"));
    if(!caseF){
      const geoF=files.find(f=>/\.(geo|geom)$/i.test(f.name));
      if(geoF){
        const geo=parseEnsightGeo(fm[geoF.name]);
        const d={...geo,name:geoF.name,dim:3,fields:{}};
        for(const f of files){if(f===geoF)continue;
          const v=parseEnsightScalar(fm[f.name],geo.nodes.length);
          if(v.length===geo.nodes.length) d.fields[f.name.replace(/\.[^.]+$/,"")]={type:"scalar",values:v};}
        let zmn=Infinity,zmx=-Infinity;
        for(const n of geo.nodes){if(n[2]<zmn)zmn=n[2];if(n[2]>zmx)zmx=n[2];}
        if(zmx-zmn<1e-10) d.dim=2;
        loadMesh(d);setActiveDemo(-1);setLog(`${geo.nodes.length} nodes, ${geo.elements.length} elems`);return;
      }
      setLog("No .case or .geo file found");return;
    }

    const cd=parseEnsightCase(fm[caseF.name]);
    if(!cd.geoFile||!fm[cd.geoFile]){
      const gn=Object.keys(fm).find(n=>/\.(geo|geom)$/i.test(n));
      if(gn) cd.geoFile=gn; else{setLog("Geo file not in upload");return;}
    }

    const geo=parseEnsightGeo(fm[cd.geoFile]);
    const d={...geo,name:caseF.name,dim:3,fields:{}};
    let zmn=Infinity,zmx=-Infinity,xyspan=0;
    for(const n of geo.nodes){
      if(n[2]<zmn)zmn=n[2]; if(n[2]>zmx)zmx=n[2];
      const ax=Math.abs(n[0]),ay=Math.abs(n[1]);
      if(ax>xyspan)xyspan=ax; if(ay>xyspan)xyspan=ay;
    }
    if(xyspan===0)xyspan=1;
    if(zmx-zmn<1e-10*xyspan) d.dim=2;

    for(const v of cd.variables){
      let fn=v.file;
      if(fn.includes("*")){
        const pat=new RegExp(fn.replace(/\./g,"\\.").replace(/\*/,"(\\d+)"));
        const ms=Object.keys(fm).filter(n=>pat.test(n)); ms.sort(); if(ms.length) fn=ms[ms.length-1];
      }
      if(!fm[fn]) continue;
      if(v.vtype==="scalar"){const vals=parseEnsightScalar(fm[fn],geo.nodes.length);if(vals.length>0)d.fields[v.name]={type:"scalar",values:vals};}
      else if(v.vtype==="vector"){const vals=parseEnsightVector(fm[fn],geo.nodes.length);if(vals.length>0)d.fields[v.name]={type:"vector",values:vals};}
    }
    loadMesh(d);setActiveDemo(-1);
    setLog(`${geo.nodes.length} nodes, ${geo.elements.length} elems, ${Object.keys(d.fields).length} fields`);
    } catch (err) {
      setLog(`Ensight load failed: ${err.message}`);
    }
  },[loadMesh]);

  const handleJSON=useCallback(e=>{const f=e.target.files?.[0];if(!f)return;const r=new FileReader();
    r.onload=ev=>{try{const d=JSON.parse(ev.target.result);if(!d.nodes||!d.elements)throw new Error("Missing");
      d.dim=d.dim||(d.nodes.every(n=>Math.abs(n[2]||0)<1e-12)?2:3);d.nodes=d.nodes.map(n=>[n[0]||0,n[1]||0,n[2]||0]);d.name=f.name;d.fields=d.fields||{};
      loadMesh(d);setActiveDemo(-1);setLog("JSON loaded");}catch(er){setLog("Error: "+er.message);}};r.readAsText(f);},[loadMesh]);

  const doExport = useCallback((fmt) => {
    if (!meshData) return;
    const EW = 800, EH = 600;
    const em = userMin!==""&&!isNaN(parseFloat(userMin)) ? parseFloat(userMin) : null;
    const ex = userMax!==""&&!isNaN(parseFloat(userMax)) ? parseFloat(userMax) : null;
    const scene = computeExportScene(meshData, activeField, showDef, defScale, camRef.current, EW, EH, displayMode, contourN, em, ex);
    if (!scene) return;
    if (fmt === "svg") {
      downloadBlob(exportSVG(scene), (meshData.name||"mesh").replace(/\.[^.]+$/,"")+".svg", "image/svg+xml");
    } else if (fmt === "eps") {
      downloadBlob(exportEPS(scene), (meshData.name||"mesh").replace(/\.[^.]+$/,"")+".eps", "application/postscript");
    } else if (fmt === "pdf") {
      downloadBlob(exportPDF(scene), (meshData.name||"mesh").replace(/\.[^.]+$/,"")+".pdf", "application/pdf");
    }
  }, [meshData, activeField, showDef, defScale, displayMode, contourN, userMin, userMax]);

  const sFields=meshData?Object.keys(meshData.fields||{}).filter(f=>meshData.fields[f].type==="scalar"):[];
  const effMin = userMin!==""&&!isNaN(parseFloat(userMin)) ? parseFloat(userMin) : fRange[0];
  const effMax = userMax!==""&&!isNaN(parseFloat(userMax)) ? parseFloat(userMax) : fRange[1];

  const cbG=useMemo(()=>{const s=[];for(let i=0;i<=20;i++){const t=i/20;const[r,g,b]=sampleTurbo(t);s.push(`rgb(${r*255|0},${g*255|0},${b*255|0}) ${t*100}%`);}return`linear-gradient(to top,${s.join(",")})`;}, []);

  // Load Computer Modern font. Pin to a specific commit SHA so a compromise of
  // the upstream repo cannot substitute new CSS into this page.
  useEffect(()=>{
    const link=document.createElement("link");
    link.rel="stylesheet";
    link.href="https://cdn.jsdelivr.net/gh/aaaakshat/cm-web-fonts@333f55ec19733c28cdc43567ecf72eafd6b0af61/fonts.css";
    document.head.appendChild(link);
    return()=>{document.head.removeChild(link);};
  },[]);

  const B=a=>({padding:"4px 10px",fontSize:11,border:a?"1px solid #4a9eff":"1px solid #333844",borderRadius:4,background:a?"#1e3a5f":"#22252e",color:a?"#6ab4ff":"#889",cursor:"pointer",whiteSpace:"nowrap"});

  return(
    <div style={{display:"flex",flexDirection:"column",height:"100vh",width:"100vw",background:"#12141a",fontFamily:"'JetBrains Mono','Fira Code','SF Mono',monospace",color:"#c8cdd5",overflow:"hidden"}}>
      <div style={{display:"flex",alignItems:"center",gap:8,padding:"5px 12px",background:"#1a1d25",borderBottom:"1px solid #2a2e38",flexShrink:0,flexWrap:"wrap",fontSize:11}}>
        <span style={{color:"#4a9eff",fontWeight:700,fontSize:13,marginRight:8}}>SIMPLAS Viewer</span>
      </div>
      <div style={{display:"flex",flex:1,overflow:"hidden"}}>
        <div style={{width:210,background:"#16181f",borderRight:"1px solid #2a2e38",padding:10,overflowY:"auto",flexShrink:0,fontSize:11}}>
          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5}}>Examples</div>
          <select style={{padding:"4px 8px",fontSize:11,border:"1px solid #333844",borderRadius:4,background:"#22252e",color:"#c8cdd5",width:"100%",marginBottom:8}}
            value={activeDemo}
            onChange={e=>{setActiveFinerDemo(-1);setActiveDemo(parseInt(e.target.value));}}>
            <option value={-1}>— Select an example —</option>
            {demos.map((d,i)=><option key={i} value={i}>{d.name}</option>)}
          </select>

          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5}}>Finer Meshes</div>
          <select style={{padding:"4px 8px",fontSize:11,border:"1px solid #333844",borderRadius:4,background:"#22252e",color:"#c8cdd5",width:"100%",marginBottom:8}}
            value={activeFinerDemo}
            onChange={e=>{setActiveDemo(-1);setActiveFinerDemo(parseInt(e.target.value));}}>
            <option value={-1}>— Select a finer mesh —</option>
            {finerDemos.map((d,i)=><option key={i} value={i}>{d.name}</option>)}
          </select>

          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5}}>Load Data</div>
          <label style={{padding:"5px 10px",fontSize:11,border:"1px dashed #4a9eff",borderRadius:4,color:"#4a9eff",cursor:"pointer",textAlign:"center",display:"block",marginBottom:4}}>
            Ensight (.case + files)<input type="file" multiple onChange={handleEnsight} style={{display:"none"}} />
          </label>
          <label style={{padding:"5px 10px",fontSize:11,border:"1px dashed #667",borderRadius:4,color:"#667",cursor:"pointer",textAlign:"center",display:"block",marginBottom:4}}>
            JSON mesh<input type="file" accept=".json" onChange={handleJSON} style={{display:"none"}} />
          </label>
          {log&&<div style={{fontSize:9,color:"#5a7",marginTop:4,lineHeight:1.4}}>{log}</div>}

          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5,marginTop:12}}>Display</div>
          {[["wireframe","Wireframe"],["plot","Contour Plot"],["lines","Contour Lines"]].map(([v,l])=>(
            <label key={v} style={{display:"flex",alignItems:"center",gap:6,padding:"2px 0",cursor:"pointer"}}>
              <input type="radio" name="dmode" checked={displayMode===v} onChange={()=>setDisplayMode(v)}/>{l}
            </label>))}
          {displayMode==="lines"&&(
            <div style={{marginTop:4}}>
              <div style={{color:"#667",fontSize:10,marginBottom:2}}>Iso-levels: {contourDraft}</div>
              {/* Drag updates draft only; commit (and full scene rebuild) happens on release. */}
              <input type="range" min="3" max="30" step="1" value={contourDraft}
                onChange={e=>setContourDraft(parseInt(e.target.value))}
                onMouseUp={e=>setContourN(parseInt(e.target.value))}
                onTouchEnd={e=>setContourN(parseInt(e.target.value))}
                onKeyUp={e=>setContourN(parseInt(e.target.value))}
                style={{width:"100%",accentColor:"#4a9eff"}}/>
            </div>
          )}

          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5,marginTop:12}}>Field</div>
          <select style={{padding:"4px 8px",fontSize:11,border:"1px solid #333844",borderRadius:4,background:"#22252e",color:"#c8cdd5",width:"100%"}} value={activeField} onChange={e=>{setActiveField(e.target.value);setUserMin("");setUserMax("");}}>
            <option value="">None</option>{sFields.map(f=><option key={f} value={f}>{f}</option>)}
          </select>
          {activeField&&(<div style={{marginTop:5}}>
            <div style={{display:"flex",gap:4,alignItems:"center",marginBottom:3}}>
              <span style={{color:"#667",fontSize:10,width:28}}>Min</span>
              <input type="text" value={userMin} placeholder={fRange[0].toExponential(2)}
                onChange={e=>setUserMin(e.target.value)}
                style={{flex:1,padding:"2px 5px",fontSize:10,border:"1px solid #333844",borderRadius:3,background:"#22252e",color:"#c8cdd5",minWidth:0}}/>
            </div>
            <div style={{display:"flex",gap:4,alignItems:"center",marginBottom:3}}>
              <span style={{color:"#667",fontSize:10,width:28}}>Max</span>
              <input type="text" value={userMax} placeholder={fRange[1].toExponential(2)}
                onChange={e=>setUserMax(e.target.value)}
                style={{flex:1,padding:"2px 5px",fontSize:10,border:"1px solid #333844",borderRadius:3,background:"#22252e",color:"#c8cdd5",minWidth:0}}/>
            </div>
            <button style={{fontSize:9,padding:"2px 8px",border:"1px solid #444",borderRadius:3,background:"#22252e",color:"#889",cursor:"pointer"}}
              onClick={()=>{setUserMin("");setUserMax("");}}>Reset to auto</button>
          </div>)}

          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5,marginTop:12}}>Deformation</div>
          <label style={{display:"flex",alignItems:"center",gap:6,padding:"2px 0",cursor:"pointer"}}>
            <input type="checkbox" checked={showDef} onChange={e=>setShowDef(e.target.checked)}/>Deformed shape
          </label>
          <div style={{color:"#667",fontSize:10,marginTop:4}}>Scale: {defScale.toFixed(1)}×</div>
          <input type="range" min="0" max="20" step="0.1" value={defScale} onChange={e=>setDefScale(parseFloat(e.target.value))} style={{width:"100%",accentColor:"#4a9eff"}}/>

          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5,marginTop:12}}>View</div>
          <button style={{...B(false),width:"100%",marginBottom:6,background:"#1a2a3a",color:"#6ab4ff",border:"1px solid #4a9eff"}} onClick={zoomToFit}>Zoom to Fit</button>
          <div style={{color:"#667",fontSize:9,marginBottom:3}}>Orthographic axes</div>
          <div style={{display:"grid",gridTemplateColumns:"1fr 1fr 1fr",gap:2,marginBottom:6}}>
          {[
            ["+X",  {theta:Math.PI/2, phi:Math.PI/2,     dist:2.5,tx:0,ty:0}],
            ["−X",  {theta:-Math.PI/2,phi:Math.PI/2,     dist:2.5,tx:0,ty:0}],
            ["+Y",  {theta:0,         phi:0.001,          dist:2.5,tx:0,ty:0}],
            ["−Y",  {theta:0,         phi:Math.PI-0.001,  dist:2.5,tx:0,ty:0}],
            ["+Z",  {theta:0,         phi:Math.PI/2,      dist:2.5,tx:0,ty:0}],
            ["−Z",  {theta:Math.PI,   phi:Math.PI/2,      dist:2.5,tx:0,ty:0}],
          ].map(([n,c])=><button key={n} style={{...B(false),fontSize:10,padding:"3px 2px"}} onClick={()=>{camRef.current={...c};}}>{n}</button>)}
          </div>
          <div style={{color:"#667",fontSize:9,marginBottom:3}}>Isometric — top</div>
          <div style={{display:"grid",gridTemplateColumns:"1fr 1fr 1fr 1fr",gap:2,marginBottom:6}}>
          {[
            ["FR", {theta:0.62,          phi:0.76,          dist:3.2,tx:0,ty:0}],
            ["FL", {theta:-0.62,         phi:0.76,          dist:3.2,tx:0,ty:0}],
            ["BR", {theta:Math.PI-0.62,  phi:0.76,          dist:3.2,tx:0,ty:0}],
            ["BL", {theta:Math.PI+0.62,  phi:0.76,          dist:3.2,tx:0,ty:0}],
          ].map(([n,c])=><button key={"it"+n} style={{...B(false),fontSize:10,padding:"3px 2px"}} onClick={()=>{camRef.current={...c};}}>{n}</button>)}
          </div>
          <div style={{color:"#667",fontSize:9,marginBottom:3}}>Isometric — bottom</div>
          <div style={{display:"grid",gridTemplateColumns:"1fr 1fr 1fr 1fr",gap:2,marginBottom:6}}>
          {[
            ["FR", {theta:0.62,          phi:Math.PI-0.76,  dist:3.2,tx:0,ty:0}],
            ["FL", {theta:-0.62,         phi:Math.PI-0.76,  dist:3.2,tx:0,ty:0}],
            ["BR", {theta:Math.PI-0.62,  phi:Math.PI-0.76,  dist:3.2,tx:0,ty:0}],
            ["BL", {theta:Math.PI+0.62,  phi:Math.PI-0.76,  dist:3.2,tx:0,ty:0}],
          ].map(([n,c])=><button key={"ib"+n} style={{...B(false),fontSize:10,padding:"3px 2px"}} onClick={()=>{camRef.current={...c};}}>{n}</button>)}
          </div>
          <div style={{color:"#667",fontSize:9,marginBottom:3}}>Elevated 45°</div>
          <div style={{display:"grid",gridTemplateColumns:"1fr 1fr 1fr 1fr",gap:2,marginBottom:6}}>
          {[
            ["F",  {theta:0,            phi:Math.PI/4,     dist:2.8,tx:0,ty:0}],
            ["B",  {theta:Math.PI,      phi:Math.PI/4,     dist:2.8,tx:0,ty:0}],
            ["R",  {theta:Math.PI/2,    phi:Math.PI/4,     dist:2.8,tx:0,ty:0}],
            ["L",  {theta:-Math.PI/2,   phi:Math.PI/4,     dist:2.8,tx:0,ty:0}],
          ].map(([n,c])=><button key={"el"+n} style={{...B(false),fontSize:10,padding:"3px 2px"}} onClick={()=>{camRef.current={...c};}}>{n}</button>)}
          </div>
          <div style={{color:"#667",fontSize:9,marginBottom:3}}>Depressed 45°</div>
          <div style={{display:"grid",gridTemplateColumns:"1fr 1fr 1fr 1fr",gap:2}}>
          {[
            ["F",  {theta:0,            phi:3*Math.PI/4,   dist:2.8,tx:0,ty:0}],
            ["B",  {theta:Math.PI,      phi:3*Math.PI/4,   dist:2.8,tx:0,ty:0}],
            ["R",  {theta:Math.PI/2,    phi:3*Math.PI/4,   dist:2.8,tx:0,ty:0}],
            ["L",  {theta:-Math.PI/2,   phi:3*Math.PI/4,   dist:2.8,tx:0,ty:0}],
          ].map(([n,c])=><button key={"dp"+n} style={{...B(false),fontSize:10,padding:"3px 2px"}} onClick={()=>{camRef.current={...c};}}>{n}</button>)}
          </div>

          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5,marginTop:14}}>Saved Views</div>
          <div style={{display:"flex",gap:3,marginBottom:5}}>
            <input type="text" placeholder="Name (optional)" value={viewName} onChange={e=>setViewName(e.target.value)}
              onKeyDown={e=>{if(e.key==="Enter")saveCurrentView();}}
              style={{flex:1,padding:"3px 6px",fontSize:10,border:"1px solid #333844",borderRadius:3,background:"#22252e",color:"#c8cdd5",minWidth:0}} />
            <button style={{padding:"3px 8px",fontSize:10,border:"1px solid #48bb78",borderRadius:3,background:"#1a3a2a",color:"#68d391",cursor:"pointer",whiteSpace:"nowrap"}}
              onClick={saveCurrentView}>Save</button>
          </div>
          {savedViews.length===0 && <div style={{fontSize:9,color:"#556",fontStyle:"italic"}}>No saved views yet.</div>}
          <div style={{display:"flex",flexDirection:"column",gap:2}}>
            {savedViews.map((v,i)=>(
              <div key={v.ts} style={{display:"flex",alignItems:"center",gap:3}}>
                <button style={{flex:1,padding:"3px 6px",fontSize:9,border:"1px solid #333844",borderRadius:3,background:"#22252e",color:"#aab",cursor:"pointer",textAlign:"left",overflow:"hidden",textOverflow:"ellipsis",whiteSpace:"nowrap"}}
                  title={`θ=${v.cam.theta.toFixed(2)} φ=${v.cam.phi.toFixed(2)} d=${v.cam.dist.toFixed(1)} — ${new Date(v.ts).toLocaleDateString()}`}
                  onClick={()=>restoreView(v)}>
                  {v.name}
                </button>
                <button style={{padding:"2px 5px",fontSize:9,border:"1px solid #553333",borderRadius:3,background:"#2a1a1a",color:"#e88",cursor:"pointer",lineHeight:1}}
                  onClick={()=>deleteView(i)} title="Delete">×</button>
              </div>
            ))}
          </div>
          {savedViews.length>0 && <div style={{fontSize:8,color:"#445",marginTop:3}}>Persisted across sessions (max 10)</div>}

          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5,marginTop:16}}>Ensight Format</div>
          <div style={{fontSize:9,color:"#556",lineHeight:1.6}}>
            Select all files at once:<br/>
            • .case (master)<br/>
            • .geo (geometry)<br/>
            • .scl / .vec / variable files<br/>
            Supports: tria3, quad4, tetra4,<br/>
            hexa8, penta6 + higher-order.<br/>
            <b style={{color:"#4a9eff"}}>3D: boundary faces only.</b>
          </div>

          <div style={{color:"#4a9eff",fontSize:11,fontWeight:600,borderBottom:"1px solid #2a2e38",paddingBottom:3,marginBottom:5,marginTop:16}}>Export</div>
          <div style={{fontSize:9,color:"#556",marginBottom:5,lineHeight:1.4}}>Vector output, white bg,<br/>flat diffuse shading.</div>
          <div style={{display:"grid",gridTemplateColumns:"1fr 1fr 1fr",gap:3}}>
            <button style={{padding:"5px 0",fontSize:11,border:"1px solid #48bb78",borderRadius:4,background:"#1a3a2a",color:"#68d391",cursor:"pointer"}} onClick={()=>doExport("svg")}>SVG</button>
            <button style={{padding:"5px 0",fontSize:11,border:"1px solid #ed8936",borderRadius:4,background:"#3a2a1a",color:"#fbd38d",cursor:"pointer"}} onClick={()=>doExport("eps")}>EPS</button>
            <button style={{padding:"5px 0",fontSize:11,border:"1px solid #e53e3e",borderRadius:4,background:"#3a1a1a",color:"#feb2b2",cursor:"pointer"}} onClick={()=>doExport("pdf")}>PDF</button>
          </div>
        </div>

        <div style={{flex:1,position:"relative"}}>
          <canvas ref={canvasRef} style={{width:"100%",height:"100%",display:"block",cursor:"grab"}}
            onMouseDown={onMD} onMouseMove={onMM} onMouseUp={onMU} onMouseLeave={onMU}
            onWheel={onWH} onContextMenu={e=>e.preventDefault()}
            onTouchStart={onTS} onTouchMove={onTM} onTouchEnd={onTE}/>
          <div style={{position:"absolute",top:8,left:8,fontSize:13,color:"#666",pointerEvents:"none",fontFamily:"'Computer Modern Serif',serif",fontWeight:700}}>{info}</div>
          {activeField&&(
            <div style={{position:"absolute",right:18,top:"50%",transform:"translateY(-50%)",display:"flex",alignItems:"center",gap:7,pointerEvents:"none",fontFamily:"'Computer Modern Serif',serif",fontWeight:700}}>
              <div style={{display:"flex",flexDirection:"column",alignItems:"flex-end",gap:0,height:220,justifyContent:"space-between"}}>
                {[0,1,2,3,4,5].map(i=>{const v=effMin+(effMax-effMin)*(1-i/5);return(
                  <span key={i} style={{fontSize:13,color:"#333",lineHeight:1}}>{v.toExponential(2)}</span>);})}
              </div>
              <div style={{width:18,height:220,borderRadius:2,border:"1px solid #999",backgroundImage:cbG}}/>
              <div style={{writingMode:"vertical-rl",textOrientation:"mixed",fontSize:14,color:"#333",letterSpacing:0.5}}>{activeField}</div>
            </div>)}
          <div style={{position:"absolute",bottom:10,right:14,fontSize:12,color:"#aaa",textAlign:"right",pointerEvents:"none",lineHeight:1.5,fontFamily:"'Computer Modern Serif',serif",fontWeight:700}}>
            Drag: rotate · Right-drag: pan · Scroll: zoom
          </div>
        </div>
      </div>
    </div>
  );
}
