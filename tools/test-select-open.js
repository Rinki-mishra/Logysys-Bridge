const { chromium } = require("playwright-core");
(async () => {
  const browser = await chromium.connectOverCDP("http://127.0.0.1:9333");
  const page = browser.contexts().flatMap((c) => c.pages()).find((p) => (p.url() || "").includes("NewHomePage"));
  if (!page) throw new Error("no home");
  const frame = page.frames().find((f) => (f.url() || "").includes("OrganizationList"));
  if (!frame) throw new Error("no list frame");

  const before = new Set(browser.contexts().flatMap((c) => c.pages()).map((p) => p.url()));

  const result = await frame.evaluate((want) => {
    const norm = (t) => (t || "").replace(/[^a-z0-9]/gi, "").toUpperCase();
    const key = norm(want);
    const h = window.DBList1_Handler;
    const rows = [...document.querySelectorAll("tr.SGridItem")];
    const idx = rows.findIndex((tr) => {
      const t = norm(tr.innerText);
      return t.includes(key) || key.includes(norm((tr.innerText || "").split("\t")[0] || ""));
    });
    if (idx < 0) return { ok: false, reason: "no-row", n: rows.length };

    const attempts = [];
    // Softlink selection APIs
    try {
      if (h.SetSelectedRowByIndex) {
        h.SetSelectedRowByIndex(idx);
        attempts.push("SetSelectedRowByIndex:" + !!(h.GetSelectedRowItem && h.GetSelectedRowItem()));
      }
    } catch (e) {
      attempts.push("SetSelectedRowByIndex-err:" + e.message);
    }
    try {
      if (h.selectgriditem) {
        h.selectgriditem(rows[idx]);
        attempts.push("selectgriditem:" + !!(h.GetSelectedRowItem && h.GetSelectedRowItem()));
      }
    } catch (e) {
      attempts.push("selectgriditem-err:" + e.message);
    }
    try {
      if (h._callRowHandler) {
        h._callRowHandler(rows[idx], "click");
        attempts.push("_callRowHandler:" + !!(h.GetSelectedRowItem && h.GetSelectedRowItem()));
      }
    } catch (e) {
      attempts.push("_callRowHandler-err:" + e.message);
    }
    try {
      // mimic grid click with Softlink ocrclick
      if (h.ocrclick) {
        h.ocrclick({ srcElement: rows[idx], target: rows[idx], preventDefault() {}, stopPropagation() {} });
        attempts.push("ocrclick:" + !!(h.GetSelectedRowItem && h.GetSelectedRowItem()));
      }
    } catch (e) {
      attempts.push("ocrclick-err:" + e.message);
    }

    // Also try clicking first cell
    try {
      const cell = rows[idx].querySelector("td") || rows[idx];
      cell.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, cancelable: true, view: window }));
      cell.dispatchEvent(new MouseEvent("mouseup", { bubbles: true, cancelable: true, view: window }));
      cell.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
      attempts.push("mouseEvents:" + !!(h.GetSelectedRowItem && h.GetSelectedRowItem()));
    } catch (e) {
      attempts.push("mouseEvents-err:" + e.message);
    }

    const selected = !!(h.GetSelectedRowItem && h.GetSelectedRowItem());
    let opened = false;
    if (selected) {
      const edit = document.getElementById("ToolBarsCtrl1_dvEdit");
      if (edit) {
        edit.click();
        opened = true;
      }
    } else {
      // try openPopupUrl / double-click path
      try {
        if (h.ocrdclick) {
          h.ocrdclick({ srcElement: rows[idx], target: rows[idx], preventDefault() {}, stopPropagation() {} });
          attempts.push("ocrdclick-fired");
          opened = true;
        }
      } catch (e) {
        attempts.push("ocrdclick-err:" + e.message);
      }
      try {
        if (h.openPopupUrl) {
          // unknown signature
          attempts.push("openPopupUrl-type:" + typeof h.openPopupUrl);
        }
      } catch (e) {}
    }

    return {
      ok: selected || opened,
      idx,
      selected,
      opened,
      attempts,
      popupFormUrl: h.popupFormUrl || null,
      selectedText: (() => {
        try {
          return h.GetSelectedText ? h.GetSelectedText() : null;
        } catch {
          return null;
        }
      })()
    };
  }, "MUNANOOR MFG");

  console.log(JSON.stringify(result, null, 2));
  await new Promise((r) => setTimeout(r, 3000));
  const after = browser.contexts().flatMap((c) => c.pages()).map((p) => p.url());
  console.log("pages", after);
  process.exit(0);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
