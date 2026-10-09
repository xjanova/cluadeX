import { readFileSync } from "node:fs";
import vm from "node:vm";
const script = readFileSync(process.argv[2], "utf8").match(/<script>([\s\S]*?)<\/script>/)[1];
function ctx(){ const els=new Map(); const el=(id)=>{ if(!els.has(id)) els.set(id,{style:{},classList:{add(){},remove(){}},appendChild(){},addEventListener(){},offsetHeight:id==="gameUI"?180:0,
  getContext:()=>new Proxy({},{get:(t,k)=>(k in t?t[k]:()=>({addColorStop(){}})),set:(t,k,v)=>((t[k]=v),true)})}); return els.get(id); };
  const sb={console,Math,document:{getElementById:el,querySelector:el,createElement:()=>el(Math.random())},window:{innerWidth:1400,innerHeight:900,addEventListener(){}},requestAnimationFrame:()=>0,setTimeout:()=>0};
  vm.createContext(sb); vm.runInContext(script,sb); return (c)=>vm.runInContext(c,sb); }
// 1) object-ball direction after a slow straight shot at a ball offset 8 px
for (const v of [4.5, 6, 9]) {
  const r = ctx();
  const res = r(`(() => { gameMode='9ball'; setupBalls(); const c={x:cueBall.x,y:cueBall.y};
    balls=[{x:c.x+200,y:c.y+8,vx:0,vy:0,radius:10,color:'#FFFF00',type:'numbered',number:9,value:1,mass:1}];
    cueBall.vx=${v}; cueBall.vy=0; ballsMoving=true; let n=0;
    while(balls[0].vx===0&&balls[0].vy===0&&n++<500) update();
    return {frames:n, deg:+(Math.atan2(balls[0].vy,balls[0].vx)*180/Math.PI).toFixed(2), cueAtContact:+(cueBall.x-c.x).toFixed(1)}; })()`);
  console.log("objectDir v="+v, res, "ideal 23.58");
}
// 2) cushion bounce direction: -60 deg toward the top rail, no balls in the way
for (const v of [8, 12]) {
  const r = ctx();
  const res = r(`(() => { gameMode='9ball'; setupBalls(); const a=-Math.PI/3;
    balls=[{x:canvas.width-TABLE_MARGIN-30,y:canvas.height-TABLE_MARGIN-30,vx:0,vy:0,radius:10,color:'#FFFF00',type:'numbered',number:9,value:1,mass:1}];
    cueBall.vx=Math.cos(a)*${v}; cueBall.vy=Math.sin(a)*${v}; ballsMoving=true; let n=0, bounce=null, pts=[];
    while(ballsMoving && n++<400){ const vy0=cueBall.vy; update(); if(!bounce && vy0<0 && cueBall.vy>0) bounce={x:cueBall.x,y:cueBall.y,n}; if(bounce && n===bounce.n+5) pts.push({x:cueBall.x,y:cueBall.y}); }
    const p=pts[0]; return {bounce:{x:+bounce.x.toFixed(1),y:bounce.y}, afterDeg:+(Math.atan2(p.y-bounce.y,p.x-bounce.x)*180/Math.PI).toFixed(2), gameOverShown: false}; })()`);
  console.log("bounce v="+v, res, "game-rule 54.21 / mirror 60.00");
}
