#!/usr/bin/env bash
# Parameter A/B renders in the open Wuling scene, composed into one labelled sheet.
#   Tools/portfolio/ab_run.sh TAG WIDTH HEIGHT VARIANTS_FILE SHOTS_FILE
# VARIANTS_FILE / SHOTS_FILE hold one entry per line in the formats documented at the top of
# ab_experiment.cs; start the variants with ("base", (System.Action)(() => { })), for the current state.
# Writes Captures/ab/TAG/<shot>__<variant>.png and Captures/ab/TAG/sheet.png (rows = shots).
# RENDER=0 only recomposes the sheet. Large sheets are colour-quantised by some image viewers: judge
# colour on crops (fitsheet.ps1 -CropList) of the single images.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$(cd "$HERE/../.." && pwd)"
tag=$1; w=$2; h=$3; vf=$(realpath "$4"); sf=$(realpath "$5")
body=$(cat "$HERE/ab_experiment.cs")
body=${body//__TAG__/$tag}
body=${body//__W__/$w}
body=${body//__H__/$h}
body=${body//__VARIANTS__/$(tr '\n' ' ' < "$vf")}
body=${body//__SHOTS__/$(tr '\n' ' ' < "$sf")}
cd "$PROJ"
if [ "${RENDER:-1}" = 1 ]; then Tools/ue.sh eval "$body" 1500; fi
vnames=$(grep -oE '^\s*\("[^"]+"' "$vf" | sed -E 's/^\s*\("//; s/"$//')
snames=$(grep -oE '^\s*\("[^"]+"' "$sf" | sed -E 's/^\s*\("//; s/"$//')
imgs=""; labels=""
for s in $snames; do for v in $vnames; do
  imgs+="Captures/ab/$tag/${s}__${v}.png|"; labels+="$s / $v|"
done; done
ncol=$(echo "$vnames" | wc -w)
powershell -NoProfile -ExecutionPolicy Bypass -File "Tools/compose.ps1" -Out "Captures/ab/$tag/sheet.png" \
  -Columns "$ncol" -CellWidth $(( 2560 / ncol )) -ImageList "${imgs%|}" -LabelList "${labels%|}"
