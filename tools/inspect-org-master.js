const WebSocket = require("ws");
const http = require("http");

function getJson(url) {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      let data = "";
      res.on("data", (c) => (data += c));
      res.on("end", () => {
        try {
          resolve(JSON.parse(data));
        } catch (e) {
          reject(e);
        }
      });
    }).on("error", reject);
  });
}

async function main() {
  const targets = await getJson("http://127.0.0.1:9333/json");
  const org = targets.find((t) => (t.url || "").includes("OrganizationMaster"));
  if (!org) {
    console.error("No OrganizationMaster target");
    process.exit(1);
  }
  console.log("Connecting", org.title, org.url);

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

  await new Promise((resolve, reject) => {
    ws.on("open", resolve);
    ws.on("error", reject);
  });

  const expr = `(() => {
    const norm = t => (t || '').replace(/\\s+/g, ' ').trim();
    const nodes = [...document.querySelectorAll('a, span, td, li, div, input, button')];
    const hits = [];
    for (const n of nodes) {
      const t = norm(n.textContent);
      if (!/account info/i.test(t)) continue;
      if (t.length > 60) continue;
      hits.push({
        tag: n.tagName,
        id: n.id || '',
        cls: (n.className || '').toString().slice(0, 120),
        text: t,
        href: (n.getAttribute('href') || '').slice(0, 200),
        onclick: (n.getAttribute('onclick') || '').slice(0, 200),
        children: n.children.length,
        parent: n.parentElement
          ? n.parentElement.tagName + '#' + (n.parentElement.id || '') + '.' + ((n.parentElement.className || '') + '').toString().slice(0, 60)
          : '',
        visible: !!(n.offsetParent)
      });
    }
    const allTabs = [...document.querySelectorAll('a, span, td')].filter(n => {
      const t = norm(n.textContent);
      return n.children.length < 2 && ['General','Shipper','Consignee','Agent','Services','Account Info','Billing','Carrier','Warehouse','Reg','IATA','Integrations','KYC'].includes(t);
    }).map(n => ({
      tag: n.tagName,
      id: n.id,
      cls: (n.className || '').toString().slice(0, 80),
      text: norm(n.textContent),
      href: (n.getAttribute('href') || '').slice(0, 160),
      onclick: (n.getAttribute('onclick') || '').slice(0, 160)
    }));
    const edits = [...document.querySelectorAll('input,button')].filter(n =>
      /^(edit|save|save & close|close)$/i.test((n.value || n.textContent || '').trim())
    ).map(n => ({
      tag: n.tagName,
      type: n.type,
      value: n.value || n.textContent,
      id: n.id,
      disabled: n.disabled,
      cls: (n.className || '').toString().slice(0, 60)
    }));
    return { hits, allTabs, edits, frames: window.frames.length, bodySample: document.body.innerText.slice(0, 300) };
  })()`;

  const r = await send("Runtime.evaluate", { expression: expr, returnByValue: true });
  console.log(JSON.stringify(r.result.value, null, 2));
  ws.close();
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
