#!/usr/bin/env bash
# Mirror what the Quest 3S wearer sees onto this Mac (view-only), over USB by default.
#
#   tools/quest_stream.sh            # left eye, cropped to the lens centre (least distortion)
#   tools/quest_stream.sh eye        # full left eye (1832x1920, fisheye edges)
#   tools/quest_stream.sh both       # the whole panel, both eyes side by side
#   tools/quest_stream.sh -r ...     # also record to Recordings/quest-<time>.mp4 (demo footage)
#   tools/quest_stream.sh -w ...     # over Wi-Fi (Quest and Mac on the same network, e.g. the iPhone hotspot);
#                                    # plug in USB once first so adb can switch the headset to TCP/IP
#   tools/quest_stream.sh -a ...     # include headset audio
#
# The Quest panel is 3664x1920 = two 1832x1920 eye buffers after lens distortion, so the raw
# picture is barrel-warped; the default crop keeps the undistorted-looking centre of the left eye.
# Needs: scrcpy (brew install scrcpy), adb, USB debugging enabled on the headset.
set -euo pipefail

record=false; wifi=false; audio=false
while getopts "rwa" opt; do
  case $opt in
    r) record=true ;;
    w) wifi=true ;;
    a) audio=true ;;
    *) sed -n '2,15p' "$0"; exit 1 ;;
  esac
done
shift $((OPTIND - 1))
view="${1:-center}"

case "$view" in
  center) crop="--crop=1400:1100:216:410" ;;   # centre of the left eye
  eye)    crop="--crop=1832:1920:0:0" ;;       # full left eye
  both)   crop="" ;;                           # whole panel
  *) echo "unknown view '$view' (center | eye | both)"; exit 1 ;;
esac

args=(--no-control --window-title="Quest 3S — AirTools" --max-fps=60 --video-bit-rate=16M --max-size=1600)
[ -n "$crop" ] && args+=("$crop")
$audio || args+=(--no-audio)
if $record; then
  root="$(cd "$(dirname "$0")/.." && pwd)"
  mkdir -p "$root/Recordings"
  out="$root/Recordings/quest-$(date +%Y%m%d-%H%M%S).mp4"
  args+=(--record="$out")
  echo "Recording to $out"
fi
$wifi && args+=(--tcpip)

if ! adb get-state >/dev/null 2>&1 && ! $wifi; then
  echo "No headset on adb. Plug in the USB cable and allow USB debugging in the headset."; exit 1
fi
echo "Streaming ($view). The picture appears once someone is wearing the headset (it sleeps otherwise). Ctrl-C to stop."
exec scrcpy "${args[@]}"
