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
  const home = targets.find((t) => (t.url || "").includes("NewHomePage")) || targets.find((t) => t.type === "page");
  console.log("page", home.title, home.url);
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

  // Dump frames
  const tree = await send("Page.getFrameTree");
  const frames = [];
  function walk(f) {
    frames.push({ id: f.frame.id, url: f.frame.url, name: f.frame.name });
    (f.childFrames || []).forEach(walk);
  }
  walk(tree.frameTree);
  console.log("frames", frames.filter((f) => /org|home|master|list/i.test(f.url + f.name)).slice(0, 20));
  console.log("frameCount", frames.length);

  const expr = `(() => {
    const edits = [...document.querySelectorAll('img,a,input,button,span,td')].filter(n => {
      const t = ((n.title||'')+' '+(n.alt||'')+' '+(n.id||'')+' '+(n.className||'')+' '+(n.value||'')+' '+(n.getAttribute('src')||'')).toLowerCase();
      return t.includes('edit') || t.includes('modify') || (n.src||'').toLowerCase().includes('edit');
    }).slice(0,40).map(n => ({
      tag:n.tagName, id:n.id, title:n.title||'', alt:n.alt||'',
      src:(n.getAttribute('src')||'').slice(0,80),
      cls:(n.className||'').toString().slice(0,60),
      value:n.value||'',
      onclick:(n.getAttribute('onclick')||'').slice(0,100)
    }));
    const toolbar = document.querySelector('[id*="toolbar" i], [class*="toolbar" i], #divToolBar, #tblToolBar');
    return {
      edits,
      hasName: !!(document.body.innerText.includes('Name') && document.body.innerText.includes('Search')),
      sample: document.body.innerText.slice(0, 400)
    };
  })()`;

  // Evaluate in main and each child frame via Runtime
  for (const frame of frames.slice(0, 15)) {
    try {
      const ctx = await send("Page.createIsolatedWorld", { frameId: frame.id, worldName: "inspect" });
      // Better: Runtime.evaluate with contextId from unique world is hard; use ExecutionContext created events
    } catch {}
  }

  const r = await send("Runtime.evaluate", { expression: expr, returnByValue: true });
  console.log("main", JSON.stringify(r.result.value, null, 2));
  ws.close();
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
