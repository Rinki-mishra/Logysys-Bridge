document.querySelector("[data-convert-form]")?.addEventListener("submit", (event) => {
    const button = event.currentTarget.querySelector("button[type=submit]");
    if (!button) {
        return;
    }

    button.disabled = true;
    button.textContent = "Creating the Logi-Sys files…";
});
