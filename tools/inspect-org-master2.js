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
  const targets = await getJson("http://127.0.0.1:9333/json");
  const org = targets.find((t) => (t.url || "").includes("OrganizationMaster"));
  if (!org) throw new Error("No OrganizationMaster");
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
    const msg = JSON.parse(d.toString());
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(JSON.stringify(msg.error)));
      else resolve(msg.result);
    }
  });
  await new Promise((r, j) => { ws.on("open", r); ws.on("error", j); });

  const expr = `(() => {
    const mainTabs = [...document.querySelectorAll('[id^="tsMainItem"]')].map(n => ({
      id: n.id, cls: n.className, onclick: n.getAttribute('onclick')||'', text: (n.innerText||'').replace(/\\s+/g,' ').trim()
    }));
    const accTabs = [...document.querySelectorAll('[id^="tabAccInfo"], [id*="Customer"], [id*="Vendor"], [id*="AccInfo"]')]
      .slice(0, 80)
      .map(n => ({ id: n.id, tag: n.tagName, cls: (n.className||'').toString().slice(0,60), text: (n.innerText||'').replace(/\\s+/g,' ').trim().slice(0,40), onclick: (n.getAttribute('onclick')||'').slice(0,80) }));
    const gl = [...document.querySelectorAll('label, td, span, input')].filter(n => /customer g\\/l|vendor g\\/l|receivable|payable/i.test((n.textContent||n.value||''))).slice(0,30)
      .map(n => ({ tag:n.tagName, id:n.id, text:(n.textContent||n.value||'').replace(/\\s+/g,' ').trim().slice(0,50) }));
    const handler = typeof tsMain_Handler !== 'undefined' ? Object.keys(tsMain_Handler) : null;
    return { mainTabs, accTabs, gl, handler, hasClickTab: !!(window.tsMain_Handler && tsMain_Handler.clickTab) };
  })()`;

  const r = await send("Runtime.evaluate", { expression: expr, returnByValue: true });
  console.log(JSON.stringify(r.result.value, null, 2));

  // Prove the Softlink API works.
  const click = await send("Runtime.evaluate", {
    expression: `(() => { try { tsMain_Handler.clickTab(5); return 'clicked-account-info'; } catch(e) { return 'err:'+e.message; } })()`,
    returnByValue: true
  });
  console.log("clickResult", click.result.value);
  ws.close();
}

main().catch((e) => { console.error(e); process.exit(1); });
