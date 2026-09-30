// Inspect the live wallpaper layer inside running ZCode via CDP.
import { writeFileSync } from "node:fs";

const port = process.argv[2] || "19788";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function findTarget() {
  for (let i = 0; i < 20; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/json/list`);
      const list = await res.json();
      const pages = list.filter((t) => t.type === "page" && !t.url.startsWith("devtools://"));
      if (pages.length) return pages;
    } catch {}
    await sleep(300);
  }
  throw new Error("no targets");
}

const pages = await findTarget();
console.log("targets:", pages.map((p) => p.url.slice(0, 60)).join(" | "));
const main = pages.find((t) => /renderer|index\.html/.test(t.url)) || pages[0];

const ws = new WebSocket(main.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
let id = 0;
const pending = new Map();
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
};
function send(method, params = {}) {
  return new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
}

const expr = `JSON.stringify((() => {
  const out = { href: location.href.slice(0, 80), iw: innerWidth, ih: innerHeight, dpr: devicePixelRatio };
  const e = document.getElementById('zcode-wallpaper');
  if (!e) { out.el = null; return out; }
  const cs = getComputedStyle(e);
  const r = e.getBoundingClientRect();
  out.el = {
    rect: { w: Math.round(r.width), h: Math.round(r.height), t: Math.round(r.top), l: Math.round(r.left) },
    bgSize: cs.backgroundSize, bgPos: cs.backgroundPosition, bgImage: cs.backgroundImage.slice(0, 60),
    opacity: cs.opacity, filter: cs.filter, transform: cs.transform, zIndex: cs.zIndex, pos: cs.position
  };
  const b = document.body; const h = document.documentElement;
  out.bodyScroll = { bw: b.scrollWidth, bh: b.scrollHeight, hw: h.scrollWidth, hh: h.scrollHeight };
  return out;
})())`;

const r = await send("Runtime.evaluate", { expression: expr, returnByValue: true });
console.log(r.result.value);
ws.close();
process.exit(0);
