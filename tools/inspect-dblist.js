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
  const home = targets.find((t) => (t.url || "").includes("NewHomePage"));
  if (!home) throw new Error("no home");
  const ws = new WebSocket(home.webSocketDebuggerUrl);
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
  await send("Page.enable");
  const tree = await send("Page.getFrameTree");
  const frames = [];
  (function walk(n) {
    frames.push(n.frame);
    (n.childFrames || []).forEach(walk);
  })(tree.frameTree);

  for (const frame of frames) {
    if (!(frame.url || "").includes("OrganizationList")) continue;
    const world = await send("Page.createIsolatedWorld", {
      frameId: frame.id,
      grantUniveralAccess: true,
      worldName: "dblist"
    });
    const r = await send("Runtime.evaluate", {
      contextId: world.executionContextId,
      returnByValue: true,
      expression: `(() => {
        const h = window.DBList1_Handler;
        const keys = h ? Object.keys(h).slice(0, 80) : [];
        let selected = null;
        try { selected = h && h.GetSelectedRowItem ? h.GetSelectedRowItem() : null; } catch(e) { selected = 'err:'+e.message; }
        const rows = [...document.querySelectorAll('tr')].filter(tr => /MUNANOOR|munanoor/i.test(tr.innerText||'')).map(tr => ({
          cls: tr.className, id: tr.id, text: (tr.innerText||'').replace(/\\s+/g,' ').trim().slice(0,120),
          onclick: (tr.getAttribute('onclick')||'').slice(0,100)
        }));
        const nameInputs = [...document.querySelectorAll('input')].filter(i => /name/i.test(i.id+i.name+(i.placeholder||''))).map(i => ({id:i.id,name:i.name,value:i.value}));
        return { keys, selected, rows, nameInputs, hasEdit: !!document.getElementById('ToolBarsCtrl1_dvEdit') };
      })()`
    });
    console.log(JSON.stringify(r.result.value, null, 2));
  }
  ws.close();
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
