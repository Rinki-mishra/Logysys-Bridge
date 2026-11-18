// Dumps the Organization Master tab strip so we can see what Account Info
// is ACTUALLY called in this tenant, instead of assuming tsMainItem6.
//
// Run with the Organization Master popup open:   node tools/inspect-tabstrip.js
// Writes tabstrip.json in the repo root.

const WebSocket = require("ws");
const http = require("http");
const fs = require("fs");
const path = require("path");

function getJson(url) {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      let data = "";
      res.on("data", (c) => (data += c));
      res.on("end", () => { try { resolve(JSON.parse(data)); } catch (e) { reject(e); } });
    }).on("error", reject);
  });
}

async function main() {
  const targets = await getJson("http://127.0.0.1:9333/json");
  const org = targets.find((t) => (t.url || "").includes("OrganizationMaster"));
  if (!org) {
    console.error("No OrganizationMaster target. Open an organisation in Softlink first.");
    process.exit(1);
  }
  console.log("Connecting:", org.url);

  const ws = new WebSocket(org.webSocketDebuggerUrl);
  let id = 0;
  const pending = new Map();
  const send = (method, params = {}) => {
    const i = ++id;
    return new Promise((resolve, reject) => {
      pending.set(i, { resolve, reject });
      ws.send(JSON.stringify({ id: i, method, params }));
    });
  };
  ws.on("message", (d) => {
    const m = JSON.parse(d.toString());
    if (m.id && pending.has(m.id)) {
      const { resolve, reject } = pending.get(m.id);
      pending.delete(m.id);
      if (m.error) reject(new Error(JSON.stringify(m.error))); else resolve(m.result);
    }
  });
  await new Promise((res, rej) => { ws.on("open", res); ws.on("error", rej); });

  const expr = `(() => {
    const norm = t => (t || '').replace(/\\s+/g, ' ').trim();

    // 1. Every tsMainItem* / tsMainLink* in document order - the crux.
    const strip = [...document.querySelectorAll('[id^="tsMainItem"], [id^="tsMainLink"]')].map(n => ({
      id: n.id,
      tag: n.tagName,
      text: norm(n.textContent).slice(0, 40),
      cls: (n.className || '').toString().slice(0, 100),
      onclick: (n.getAttribute('onclick') || '').slice(0, 200),
      href: (n.getAttribute('href') || '').slice(0, 200),
      visible: !!n.offsetParent,
      innerA: n.querySelector('a') ? (n.querySelector('a').id || 'anon') : null
    }));

    // 2. Which element actually carries the text "Account Info"?
    const accountInfo = [...document.querySelectorAll('a,td,span,li,div')]
      .filter(n => norm(n.textContent) === 'Account Info')
      .map(n => ({
        tag: n.tagName, id: n.id || '(none)',
        cls: (n.className || '').toString().slice(0, 100),
        onclick: (n.getAttribute('onclick') || '').slice(0, 200),
        href: (n.getAttribute('href') || '').slice(0, 200),
        visible: !!n.offsetParent,
        parentId: n.parentElement ? (n.parentElement.id || n.parentElement.tagName) : ''
      }));

    // 3. Task buttons and their enabled state (view vs edit mode).
    const buttons = [...document.querySelectorAll('input,button,a')]
      .filter(n => /^(edit|save|save & close|save and close|close)$/i.test(norm(n.value || n.textContent)))
      .map(n => ({ id: n.id || '(none)', tag: n.tagName, label: norm(n.value || n.textContent),
                   disabled: !!n.disabled, visible: !!n.offsetParent }));

    // 4. Is the form currently in edit mode, and is anything in an iframe?
    return {
      url: location.href,
      frames: window.frames.length,
      iframeSrcs: [...document.querySelectorAll('iframe')].map(f => f.src).slice(0, 5),
      strip, accountInfo, buttons,
      hasCodText: (document.body.innerText || '').includes('Cash On Delivery'),
      activeTab: (strip.find(s => (s.cls || '').includes('linkOn')) || {}).id || '(none has linkOn)'
    };
  })()`;

  const r = await send("Runtime.evaluate", { expression: expr, returnByValue: true });
  const out = path.join(__dirname, "..", "tabstrip.json");
  fs.writeFileSync(out, JSON.stringify(r.result.value, null, 2));
  console.log("Wrote", out);
  console.log(JSON.stringify(r.result.value, null, 2));
  ws.close();
}

main().catch((e) => { console.error(e); process.exit(1); });
