import { readFileSync } from "node:fs";
import vm from "node:vm";
const html = readFileSync(process.argv[2], "utf8");
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const els = new Map();
const el = (id) => { if (!els.has(id)) els.set(id, { style:{}, classList:{add(){},remove(){}}, appendChild(){}, addEventListener(){}, offsetHeight: id==="gameUI"?180:0,
  getContext: () => new Proxy({}, { get: (t,k)=> (k in t ? t[k] : () => ({addColorStop(){}})), set:(t,k,v)=>((t[k]=v),true) }) }); return els.get(id); };
const sb = { console, Math, document:{getElementById:el, querySelector:el, createElement:()=>el(Math.random())}, window:{innerWidth:1400,innerHeight:900,addEventListener(){}}, requestAnimationFrame:()=>0, setTimeout:()=>0 };
vm.createContext(sb); vm.runInContext(script, sb);
const r = (c) => vm.runInContext(c, sb);
for (const mode of ["snooker","9ball"]) {
  r(`gameMode='${mode}'; setupBalls();`);
  const overl = r(`(() => { const all=[cueBall,...balls]; const o=[]; for(let i=0;i<all.length;i++) for(let j=i+1;j<all.length;j++){ const d=Math.hypot(all[i].x-all[j].x, all[i].y-all[j].y); if (d < all[i].radius+all[j].radius) o.push((all[i].type==='numbered'?all[i].number:all[i].type)+'~'+(all[j].type==='numbered'?all[j].number:all[j].type)+' d='+d.toFixed(1)); } return o; })()`);
  // nudge: start a gentle shot away from the pack and see who moves on frame 1
  r(`cueBall.vx=-0.5; cueBall.vy=0; ballsMoving=true; update();`);
  const movers = r(`balls.filter(b=>b.vx||b.vy).map(b=>(b.number||b.type)+':'+Math.hypot(b.vx,b.vy).toFixed(1))`);
  console.log(mode, { overlapsAtRack: overl, movingAfterOneFrameOfAGentleShotAway: movers });
}
