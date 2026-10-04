const video = document.querySelector("video");
const error = document.querySelector("[data-video-error]");
if (video && error) {
    video.addEventListener("error", () => { error.hidden = false; });
    video.addEventListener("loadedmetadata", () => { error.hidden = true; });
}
const track = document.querySelector("video track");
const subtitleError = document.querySelector("[data-subtitle-error]");
if (track && subtitleError) {
    track.addEventListener("error", () => { subtitleError.hidden = false; });
    track.addEventListener("load", () => { subtitleError.hidden = true; });
}
