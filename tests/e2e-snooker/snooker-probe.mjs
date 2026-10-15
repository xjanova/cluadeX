// Headless probe of D:\GameProject\Snooker2D\index.html game logic (read-only: loads the file, never writes it).
// Usage: node snooker-probe.mjs <path-to-index.html>
import { readFileSync } from "node:fs";
import vm from "node:vm";

const html = readFileSync(process.argv[2], "utf8");
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

function makeCtx() {
  const els = new Map();
  const el = (id) => {
    if (!els.has(id)) {
      els.set(id, {
        id, style: {}, textContent: "", innerHTML: "", offsetHeight: id === "gameUI" ? 180 : 0,
        width: 0, height: 0,
        classList: { _s: new Set(), add(c) { this._s.add(c); }, remove(c) { this._s.delete(c); } },
        appendChild() {}, addEventListener() {},
        getContext: () => new Proxy({}, { get: (t, k) => (k in t ? t[k] : () => ({ addColorStop() {} })), set: (t, k, v) => ((t[k] = v), true) }),
        getBoundingClientRect: () => ({ left: 0, top: 0 }),
      });
    }
    return els.get(id);
  };
  const timers = [];
  const sandbox = {
    console,
    document: { getElementById: el, querySelector: (q) => el(q), createElement: () => el("tmp" + Math.random()) },
    window: { innerWidth: 1400, innerHeight: 900, addEventListener() {} },
    requestAnimationFrame: () => 0, // init() runs gameLoop once; we step update() by hand
    setTimeout: (fn, ms) => { timers.push(fn); return timers.length; },
    Math,
  };
  vm.createContext(sandbox);
  vm.runInContext(script, sandbox);
  return { sandbox, run: (code) => vm.runInContext(code, sandbox), timers };
}

function stepUntilStill(g, max = 2000) {
  let n = 0;
  while (g.run("ballsMoving") && n < max) { g.run("update()"); n++; }
  return n;
}

const out = {};

// A. cue ball potted (snooker) — does the turn pass to the opponent?
{
  const g = makeCtx();
  g.run(`gameMode='snooker'; currentPlayer=1; player1Score=0; player2Score=0; setupBalls();
         const p = pockets[0]; cueBall.x = p.x + 40; cueBall.y = p.y + 40; cueBall.vx = -6; cueBall.vy = -6; ballsMoving = true;`);
  const frames = stepUntilStill(g);
  out.A_cueFoul = { frames, currentPlayerAfter: g.run("currentPlayer"), p1: g.run("player1Score"), p2: g.run("player2Score"), expected: "currentPlayer 2" };
}

// B. legal colour pot in snooker — is the colour respotted on its spot?
{
  const g = makeCtx();
  g.run(`gameMode='snooker'; currentPlayer=1; player1Score=0; player2Score=0; setupBalls(); snookerPhase='color';
         var y = balls.find(b=>b.type==='yellow'); var spot = {x:y.x, y:y.y};
         const p = pockets[2]; y.x = p.x - 40; y.y = p.y + 40; y.vx = 6; y.vy = -6;
         cueBall.vx = 0; cueBall.vy = 0; ballsMoving = true;`);
  // also keep another ball rolling so the shot lasts longer than the pot
  g.run(`var r = balls.find(b=>b.type==='red'); r.vx = 0; r.vy = 0;`);
  const frames = stepUntilStill(g);
  const yellowAfterShot = g.run(`JSON.stringify(balls.filter(b=>b.type==='yellow').map(b=>({x:Math.round(b.x),y:Math.round(b.y)})))`);
  const spot = g.run("JSON.stringify({x:Math.round(spot.x),y:Math.round(spot.y)})");
  const pocket = g.run("JSON.stringify(pockets[2])");
  const s1 = { p1: g.run("player1Score"), currentPlayer: g.run("currentPlayer"), phase: g.run("snookerPhase") };
  // next shot: any movement at all
  g.run(`cueBall.vx = 3; cueBall.vy = 0; ballsMoving = true;`);
  stepUntilStill(g);
  const yellowAfterNextShot = g.run(`balls.filter(b=>b.type==='yellow').length`);
  out.B_colourRespot = { frames, spot, pocket, yellowAfterShot, afterShot: s1, yellowCountAfterNextShot: yellowAfterNextShot, currentPlayerAfterNextShot: g.run("currentPlayer") };
}

// C. power meter — does it ping-pong or wrap to 0?
{
  const g = makeCtx();
  g.run(`ballsMoving=false; powerCharging=true; power=0;`);
  const seq = [];
  for (let i = 0; i < 60; i++) { g.run("update()"); if (i >= 46) seq.push(g.run("power")); }
  out.C_power = { framesAt46to59: seq, maxShotSpeedPxPerFrame: 98 * 0.3 };
}

// D. 9-ball cue foul
{
  const g = makeCtx();
  g.run(`gameMode='9ball'; currentPlayer=1; setupBalls();
         const p = pockets[3]; cueBall.x = p.x + 40; cueBall.y = p.y - 40; cueBall.vx = -6; cueBall.vy = 6; ballsMoving = true;`);
  stepUntilStill(g);
  out.D_9ballCueFoul = { currentPlayerAfter: g.run("currentPlayer"), expected: 2 };
}

// E. globals reachable by name from page-context eval (what a browser JS check can read)
{
  const g = makeCtx();
  out.E_globals = g.run(`JSON.stringify({ canvas:[canvas.width,canvas.height], balls: balls.length, gameMode, playerMode, currentPlayer,
     cue: {x:cueBall.x,y:cueBall.y}, pockets: pockets.length, fnOnWindow: typeof update })`);
}

// F. thin-cut miss at max power (tunnelling): cue passes an object ball at 15 px offset at 29.4 px/frame
{
  const g = makeCtx();
  g.run(`gameMode='9ball'; setupBalls(); balls = [ {x:400,y:215,vx:0,vy:0,radius:10,color:'#FFFF00',type:'numbered',number:1,value:1,mass:1},
                                                  {x:900,y:400,vx:0,vy:0,radius:10,color:'#FFFF00',type:'numbered',number:9,value:1,mass:1} ];
         cueBall.x = 300; cueBall.y = 200; cueBall.vx = 29.4; cueBall.vy = 0; ballsMoving = true;`);
  let touched = false;
  for (let i = 0; i < 12; i++) { g.run("update()"); if (g.run("balls[0].vx !== 0 || balls[0].vy !== 0")) touched = true; }
  out.F_tunnel = { objectBallMovedAtMaxPower: touched, note: "geometric overlap exists (offset 15 < 20)" };
}

console.log(JSON.stringify(out, null, 2));
