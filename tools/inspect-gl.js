const WebSocket = require("ws");
const http = require("http");
function getJson(url) {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      let data = "";
      res.on("data", (c) => (data += c));
      res.on("end", () => resolve(JSON.parse(data)));
    }).on("error", reject);
  });
}
async function main() {
  const targets = await getJson("http://127.0.0.1:9333/json/list");
  const org = targets.find((t) => (t.url || "").includes("OrganizationMaster"));
  const ws = new WebSocket(org.webSocketDebuggerUrl);
  let id = 0;
  const pending = new Map();
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const i = ++id;
    pending.set(i, { resolve, reject });
    ws.send(JSON.stringify({ id: i, method, params }));
  });
  ws.on("message", (d) => {
    const msg = JSON.parse(d.toString());
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(JSON.stringify(msg.error)));
      else resolve(msg.result);
    }
  });
  await new Promise((r, j) => { ws.on("open", r); ws.on("error", j); });
  const r = await send("Runtime.evaluate", {
    expression: `(() => {
      const pick = (sel) => [...document.querySelectorAll(sel)].slice(0, 60).map(n => ({
        id: n.id, tag: n.tagName, type: n.type||'', value: (n.value||'').slice(0,40),
        title: n.title||'', alt: n.alt||'', src: (n.getAttribute('src')||'').slice(0,80),
        cls: (n.className||'').toString().slice(0,60),
        text: (n.textContent||'').replace(/\\s+/g,' ').trim().slice(0,40)
      }));
      return {
        glInputs: pick('input[id*="GL" i], input[id*="Gl"], input[id*="Cust"], input[id*="Vend"], img[id*="GL" i], img[src*="search" i], img[title*="Search" i], a[id*="GL" i]'),
        nearCust: (() => {
          const label = [...document.querySelectorAll('td,span,label')].find(n => (n.textContent||'').replace(/\\s+/g,' ').trim() === 'Customer G/L');
          if (!label) return null;
          const row = label.closest('tr') || label.parentElement;
          return row ? row.innerHTML.slice(0, 500) : null;
        })(),
        nearVend: (() => {
          const label = [...document.querySelectorAll('td,span,label')].find(n => (n.textContent||'').replace(/\\s+/g,' ').trim() === 'Vendor G/L');
          if (!label) return null;
          const row = label.closest('tr') || label.parentElement;
          return row ? row.innerHTML.slice(0, 500) : null;
        })(),
        receivable: (() => {
          const n = [...document.querySelectorAll('td,label,span')].find(x => (x.textContent||'').replace(/\\s+/g,' ').trim() === 'Receivable');
          if (!n) return null;
          const scope = n.closest('tr,td,div,label') || n.parentElement;
          const cb = scope && scope.querySelector('input[type=checkbox]');
          return { id: cb && cb.id, checked: cb && cb.checked };
        })()
      };
    })()`,
    returnByValue: true
  });
  console.log(JSON.stringify(r.result.value, null, 2));
  ws.close();
}
main().catch((e) => { console.error(e); process.exit(1); });
