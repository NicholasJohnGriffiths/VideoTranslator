const audio = document.querySelector("audio");
const error = document.querySelector("[data-audio-error]");
audio.addEventListener("error", () => { error.hidden = false; });
audio.querySelector("source").addEventListener("error", () => { error.hidden = false; });
audio.addEventListener("loadedmetadata", () => { error.hidden = true; });
