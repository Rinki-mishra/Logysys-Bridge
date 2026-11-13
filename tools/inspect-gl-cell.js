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
(async () => {
  const targets = await getJson("http://127.0.0.1:9333/json/list");
  const org = targets.find((t) => (t.url || "").includes("OrganizationMaster"));
  const ws = new WebSocket(org.webSocketDebuggerUrl);
  let id = 0;
  const pending = new Map();
  const send = (method, params = {}) =>
    new Promise((resolve, reject) => {
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
  await new Promise((r, j) => {
    ws.on("open", r);
    ws.on("error", j);
  });
  const r = await send("Runtime.evaluate", {
    expression: `(() => {
      const td = document.getElementById('tdCustGL');
      const imgs = td ? [...td.querySelectorAll('img,a,input')].map(n => ({
        id: n.id, tag: n.tagName, src: (n.getAttribute('src') || ''),
        onclick: (n.getAttribute('onclick') || '').slice(0, 150),
        value: n.value || '', type: n.type || ''
      })) : [];
      const inputs = td ? [...td.querySelectorAll('input')].map(n => ({ id: n.id, type: n.type, value: n.value })) : [];
      return { html: td ? td.innerHTML.slice(0, 1500) : 'none', imgs, inputs };
    })()`,
    returnByValue: true
  });
  console.log(JSON.stringify(r.result.value, null, 2));
  ws.close();
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
