document.querySelectorAll("textarea[data-script-edit]").forEach(input => {
    input.addEventListener("input", () => {
        input.closest("article").querySelector('input[type="checkbox"]').checked = false;
        clearPreview(input);
    });
});

document.querySelectorAll(".transcript-segment input[type='checkbox']").forEach(input => {
    input.addEventListener("change", () => clearPreview(input));
});

document.querySelectorAll("[data-voice-preview]").forEach(preview => {
    const reportError = () => {
        preview.querySelector("[data-preview-error]").hidden = false;
    };
    preview.querySelector("audio").addEventListener("error", reportError);
    preview.querySelector("source").addEventListener("error", reportError);
    preview.querySelector("audio").addEventListener("loadedmetadata", () => {
        preview.querySelector("[data-preview-error]").hidden = true;
    });
});

function clearPreview(input) {
    const preview = input.closest("article").querySelector("[data-voice-preview]");
    if (preview) {
        preview.querySelector("audio").pause();
        preview.remove();
    }
}

document.querySelector("form.surface")?.addEventListener("submit", event => {
    const submitter = event.submitter;
    if (submitter?.formAction.includes("handler=Preview")) {
        const status = document.createElement("p");
        status.setAttribute("role", "status");
        status.textContent = "Generating voice preview. Please wait; do not submit again.";
        submitter.insertAdjacentElement("afterend", status);
    }
});
