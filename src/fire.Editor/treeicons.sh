#!/bin/sh
# Builds the icons of the solution explorer (Assets/treeicons) from the file type icons in "filetypes hires/png":
# 32 px (box filter, the grid of the pixel art) for screens with a scaling of 1.5 and more, 16 px (box filter over the 32 px, slightly sharpened) for the others.
# Needs ImageMagick. Usage (in src/fire.Editor):   sh treeicons.sh
set -e
cd "$(dirname "$0")"
mkdir -p Assets/treeicons
for n in biscuit file fireproj firescript firesln firexml folder folderbase md xmd; do
  convert "filetypes hires/png/$n.png" -filter box -resize 32x32 -strip PNG32:"Assets/treeicons/${n}_32.png"
  convert "filetypes hires/png/$n.png" -filter box -resize 32x32 -filter box -resize 16x16 -unsharp 0x0.6+0.8+0 -strip PNG32:"Assets/treeicons/${n}_16.png"
done
echo "Assets/treeicons: done"
