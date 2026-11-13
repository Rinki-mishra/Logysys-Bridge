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
  const version = await getJson("http://127.0.0.1:9333/json/version");
  console.log("browserWS", version.webSocketDebuggerUrl);
  const targets = await getJson("http://127.0.0.1:9333/json/list");
  console.log("targets", targets.map(t => ({ type: t.type, title: t.title, url: (t.url||"").slice(0,80) })));

  // Click Account Info via LI native click (not direct clickTab) and Edit button.
  const org = targets.find((t) => (t.url || "").includes("OrganizationMaster"));
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

  const clickExpr = `(() => {
    const li = document.getElementById('tsMainItem6');
    if (!li) return 'no-li';
    // Softlink clickTab uses arguments.caller — must invoke via DOM event, not direct call from strict eval.
    li.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
    return 'li-clicked cls=' + li.className;
  })()`;
  const r1 = await send("Runtime.evaluate", { expression: clickExpr, returnByValue: true });
  console.log("accountClick", r1.result.value);

  await new Promise(r => setTimeout(r, 1000));

  const editExpr = `(() => {
    const btn = document.getElementById('TaskButtons1_dvEdit');
    if (!btn) return 'no-edit';
    if (btn.disabled || btn.classList.contains('aspNetDisabled')) return 'edit-disabled';
    btn.click();
    return 'edit-clicked disabled=' + btn.disabled;
  })()`;
  const r2 = await send("Runtime.evaluate", { expression: editExpr, returnByValue: true });
  console.log("editClick", r2.result.value);

  await new Promise(r => setTimeout(r, 1500));

  const state = await send("Runtime.evaluate", {
    expression: `(() => ({
      accountCls: (document.getElementById('tsMainItem6')||{}).className,
      generalCls: (document.getElementById('tsMainItem1')||{}).className,
      editDisabled: !!(document.getElementById('TaskButtons1_dvEdit')||{}).disabled,
      saveDisabled: !!(document.getElementById('TaskButtons1_dvSave')||{}).disabled,
      saveCloseDisabled: !!(document.getElementById('TaskButtons1_dvSaveClose')||{}).disabled,
      customerOn: (document.getElementById('tabCustVendAccInfoItem1')||{}).className,
      hasCustGl: !!(document.body.innerText.includes('Customer G/L'))
    }))()`,
    returnByValue: true
  });
  console.log("state", state.result.value);
  ws.close();
}

main().catch((e) => { console.error(e); process.exit(1); });
