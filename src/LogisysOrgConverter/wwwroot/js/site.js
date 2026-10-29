const loadSheet = document.querySelector("[data-load-sheet]");
loadSheet?.addEventListener("click", async () => {
    const form = loadSheet.closest("form");
    const input = form?.querySelector("input[type=file]");
    const status = form?.querySelector("[data-load-status]");
    const token = form?.querySelector("input[name=__RequestVerificationToken]")?.value;
    if (!input || !status) {
        return;
    }

    loadSheet.disabled = true;
    const label = loadSheet.textContent;
    loadSheet.textContent = "Opening the sheet…";
    status.textContent = "Edge is opening the spreadsheet from the mail. The verification code is read from the mailbox and entered. Check the taskbar if the window is behind this one.";
    try {
        const response = await fetch(loadSheet.dataset.loadSheet, {
            method: "POST",
            headers: token ? { RequestVerificationToken: token } : {}
        });
        if (!response.ok) {
            const text = (await response.text()).trim();
            status.textContent = text.startsWith("System.") || text.includes("<html") || text.length > 400
                ? "The spreadsheet could not be loaded. Load it again."
                : text;
            return;
        }

        const blob = await response.blob();
        const name = response.headers.get("X-Sheet-Name") || "Organisation Creation Request Form (Responses).xlsx";
        const file = new File([blob], name, { type: blob.type || "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" });
        const transfer = new DataTransfer();
        transfer.items.add(file);
        input.files = transfer.files;
        status.textContent = name + " is in Choose File. Click Create Logi-Sys files.";
    } catch {
        status.textContent = "The spreadsheet could not be loaded from the mailbox.";
    } finally {
        loadSheet.disabled = false;
        loadSheet.textContent = label;
    }
});

document.querySelectorAll("[data-convert-form]").forEach((form) => {
    form.addEventListener("submit", (event) => {
        const button = event.currentTarget.querySelector("button[type=submit]");
        if (!button) {
            return;
        }

        button.disabled = true;
        button.textContent = "Creating the Logi-Sys files…";
    });
});
