const video = document.querySelector("video");
const error = document.querySelector("[data-video-error]");
if (video && error) {
    video.addEventListener("error", () => { error.hidden = false; });
    video.addEventListener("loadedmetadata", () => { error.hidden = true; });
}
const track = document.querySelector("video track");
const subtitleError = document.querySelector("[data-subtitle-error]");
const subtitleToggle = document.querySelector("[data-subtitle-toggle]");
const subtitleControls = document.querySelector("[data-subtitle-controls]");
if (video && track && subtitleToggle && subtitleControls) {
    track.track.mode = "showing";
    subtitleControls.hidden = false;
    subtitleToggle.addEventListener("change", () => {
        track.track.mode = subtitleToggle.checked ? "showing" : "disabled";
    });
    video.textTracks.addEventListener("change", () => {
        subtitleToggle.checked = track.track.mode === "showing";
    });
}
if (track && subtitleError) {
    track.addEventListener("error", () => { subtitleError.hidden = false; });
    track.addEventListener("load", () => { subtitleError.hidden = true; });
}
