#!/usr/bin/env bash
set -euo pipefail

cd /home/site/wwwroot
media="/home/videotranslator-media/n9.0-b28d9af79527"
mkdir -p "$media"
for name in ffmpeg ffprobe; do
    if [ ! -x "$media/$name" ]; then
        cp "tools/bin/$name" "$media/$name.tmp"
        chmod 700 "$media/$name.tmp"
        mv "$media/$name.tmp" "$media/$name"
    fi
done
"$media/ffmpeg" -version >/dev/null
"$media/ffprobe" -version >/dev/null
export Media__FFmpegPath="$media/ffmpeg"
export Media__FFprobePath="$media/ffprobe"
exec dotnet VideoTranslator.dll
