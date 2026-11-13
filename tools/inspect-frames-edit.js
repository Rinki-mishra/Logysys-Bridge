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

  await send("Runtime.enable");
  await send("Page.enable");
  const tree = await send("Page.getFrameTree");
  const frameList = [];
  (function walk(n) {
    frameList.push(n.frame);
    (n.childFrames || []).forEach(walk);
  })(tree.frameTree);

  for (const frame of frameList) {
    const expr = `(() => {
      const text = (document.body && document.body.innerText || '').slice(0, 200);
      const hasSearch = /\\bSearch\\b/.test(text) && /\\bName\\b/.test(text);
      const edits = [...document.querySelectorAll('img,input,a,button')].filter(n => {
        const blob = ((n.title||'')+' '+(n.alt||'')+' '+(n.id||'')+' '+(n.className||'')+' '+(n.value||'')+' '+(n.getAttribute('src')||'')+' '+(n.getAttribute('onclick')||'')).toLowerCase();
        return blob.includes('edit') || blob.includes('modify');
      }).slice(0, 25).map(n => ({
        tag: n.tagName, id: n.id, title: n.title||'', alt: n.alt||'', value: n.value||'',
        src: (n.getAttribute('src')||'').slice(0,100),
        onclick: (n.getAttribute('onclick')||'').slice(0,120),
        visible: !!(n.offsetParent)
      }));
      return { hasSearch, edits, url: location.href, text: text.replace(/\\s+/g,' ').slice(0,180) };
    })()`;
    try {
      // Attach to frame execution context via Page.createIsolatedWorld then evaluate there
      const world = await send("Page.createIsolatedWorld", {
        frameId: frame.id,
        grantUniveralAccess: true,
        worldName: "editInspect"
      });
      const r = await send("Runtime.evaluate", {
        expression: expr,
        returnByValue: true,
        contextId: world.executionContextId
      });
      const v = r.result.value;
      if (v && (v.hasSearch || (v.edits && v.edits.length))) {
        console.log("---", frame.url.slice(0, 100));
        console.log(JSON.stringify(v, null, 2));
      }
    } catch (e) {
      // ignore cross-origin / missing frames
    }
  }
  ws.close();
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
