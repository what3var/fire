#!/usr/bin/env bash
# tools/setup-retro.sh - installs and sets up what the retro targets of fire need to be built and tested on Linux (Ubuntu 24.04 / Debian; as root or
# with sudo): Open Watcom for dos16, dos32, win16 and bios16, DOSBox-X (DOS), QEMU (the BIOS, the long mode without DOS, FreeDOS), Wine with its 32-bit
# part and Xvfb (Windows 3.x programs), nasm, mtools and dosfstools (boot sectors and floppy images), MinGW (fire.exe for `conformance --wine`).
# It can be run again at any time: what is there is left as it is.
#
#   tools/setup-retro.sh                     everything (packages, Open Watcom into /opt/watcom, the prefix of Wine)
#   tools/setup-retro.sh --check             only says what is there and what is missing (changes nothing; exit code 1 when something is missing)
#   tools/setup-retro.sh --watcom-dir <dir>  Open Watcom into another folder (forge then needs WATCOM=<dir>; /opt/watcom is found without it)
#   tools/setup-retro.sh --no-packages       no apt-get (only Open Watcom, FreeDOS and the prefix of Wine)
#   tools/setup-retro.sh --freedos           also the image of FreeDOS 1.4 (FD14LITE.img) for the cases under QEMU with a 64-bit processor
#   tools/setup-retro.sh --forge <dir>       also forge-c of this checkout in <dir>, laid out like a distribution: native/ and Source/ (links into the
#                                            checkout) and the standard packages installed into <dir>/Packages - no FIRE_* variables needed
#
# Then (docs/tools/testing.md, "The retro targets"):
#   <dir>/forge-c conformance retro -j 4     the suite tests/retro: build each case with Open Watcom, run it in DOSBox-X, QEMU or Wine
set -u

WATCOM_DIR=/opt/watcom
WATCOM_URL=${WATCOM_URL:-https://github.com/open-watcom/open-watcom-v2/releases/download/Current-build/ow-snapshot.tar.xz}
FREEDOS_URL=${FREEDOS_URL:-https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/distributions/1.4/FD14-LiteUSB.zip}
CACHE=${XDG_CACHE_HOME:-$HOME/.cache}/fire
WINEPREFIX_RETRO=${FIRE_RETRO_WINEPREFIX:-$CACHE/retro-wine}
check=0 packages=1 freedos=0 forgedir=
REPO=$(cd "$(dirname "$0")/.." && pwd)
while [ $# -gt 0 ]; do
    case "$1" in
        --check) check=1 ;;
        --watcom-dir) WATCOM_DIR=$2; shift ;;
        --no-packages) packages=0 ;;
        --freedos) freedos=1 ;;
        --forge) forgedir=$2; shift ;;
        -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1 (--help)" >&2; exit 2 ;;
    esac
    shift
done

say() { printf '\033[1m%s\033[0m\n' "$*"; }
have() { command -v "$1" > /dev/null 2>&1; }
SUDO=
if [ "$(id -u)" -ne 0 ]; then have sudo && SUDO=sudo; fi

# ---- what is there
# owcc as forge finds it: the PATH, $WATCOM, /opt/watcom (binl64, then binl)
watcom_owcc() {
    local d
    if have owcc; then command -v owcc; return 0; fi
    for d in "${WATCOM:-}" "$WATCOM_DIR" /opt/watcom; do
        [ -n "$d" ] || continue
        [ -x "$d/binl64/owcc" ] && { echo "$d/binl64/owcc"; return 0; }
        [ -x "$d/binl/owcc" ] && { echo "$d/binl/owcc"; return 0; }
    done
    return 1
}
# wine with its 32-bit part: Windows 3.x programs run through the 16-bit support of the 32-bit Wine
wine32_ok() { have wine && { [ -e /usr/lib/i386-linux-gnu/wine ] || [ -e /usr/lib/wine/i386-windows ] || dpkg -s wine32:i386 > /dev/null 2>&1; }; }
report() {
    local missing=0 p
    say "fire retro targets: what this machine has"
    if o=$(watcom_owcc); then echo "  ok      Open Watcom      $o"; else echo "  missing Open Watcom      (owcc)"; missing=1; fi
    for p in dosbox-x qemu-system-i386 qemu-system-x86_64 xvfb-run nasm mcopy mtype fsck.fat x86_64-w64-mingw32-gcc; do
        if have $p; then echo "  ok      $p"; else echo "  missing $p"; missing=1; fi
    done
    if wine32_ok; then echo "  ok      wine (with 32-bit)  $(wine --version 2>/dev/null)"; else echo "  missing wine with its 32-bit part (wine32:i386)"; missing=1; fi
    if [ -f "$WINEPREFIX_RETRO/system.reg" ]; then echo "  ok      prefix of Wine   $WINEPREFIX_RETRO"; else echo "  missing prefix of Wine   $WINEPREFIX_RETRO (made by this script, or on the first run)"; fi
    if [ -f "$CACHE/freedos/FD14LITE.img" ] || [ -n "${FIRE_FREEDOS_IMAGE:-}" ]; then echo "  ok      FreeDOS image    ${FIRE_FREEDOS_IMAGE:-$CACHE/freedos/FD14LITE.img}"
    else echo "  -       FreeDOS image    (optional: --freedos)"; fi
    return $missing
}
if [ $check -eq 1 ]; then report; exit $?; fi

# ---- the packages of the system
if [ $packages -eq 1 ]; then
    if ! have apt-get; then
        say "no apt-get here: install yourself - Open Watcom (this script does that below), DOSBox-X, QEMU (i386, x86_64), Wine with 32-bit, Xvfb, nasm, mtools, dosfstools, MinGW-w64"
    else
        say "packages (apt-get)"
        if ! dpkg --print-foreign-architectures | grep -qx i386; then $SUDO dpkg --add-architecture i386 || exit 1; fi
        $SUDO apt-get update -q || exit 1
        # (DOSBox-X is in Ubuntu 24.04 and Debian 13; elsewhere apt-get leaves it out and the line below says so)
        DEBIAN_FRONTEND=noninteractive $SUDO apt-get install -y -q --no-install-recommends \
            ca-certificates curl xz-utils unzip gcc make \
            qemu-system-x86 xvfb xauth nasm mtools dosfstools mingw-w64 \
            wine wine64 wine32:i386 || exit 1
        DEBIAN_FRONTEND=noninteractive $SUDO apt-get install -y -q --no-install-recommends dosbox-x \
            || say "DOSBox-X is not in the package sources of this system: install it another way (flatpak com.dosbox_x.DOSBox-X, or from dosbox-x.com) and put dosbox-x on the PATH"
    fi
fi

# ---- Open Watcom (the snapshot of open-watcom-v2: binl64 for Linux x64, the headers and libraries of DOS, Windows 3.x and DOS/32A)
if o=$(watcom_owcc); then
    say "Open Watcom: already there ($o)"
else
    say "Open Watcom: $WATCOM_URL -> $WATCOM_DIR"
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    curl -fL --retry 3 -o "$tmp/ow.tar.xz" "$WATCOM_URL" || { echo "download failed"; exit 1; }
    $SUDO mkdir -p "$WATCOM_DIR" || exit 1
    $SUDO tar -xJf "$tmp/ow.tar.xz" -C "$WATCOM_DIR" || { echo "unpacking failed"; exit 1; }
    o=$(watcom_owcc) || { echo "no owcc in $WATCOM_DIR after unpacking"; exit 1; }
    echo "  $o"
fi
if [ "$WATCOM_DIR" != /opt/watcom ] && [ -z "${WATCOM:-}" ] && ! have owcc; then
    say "forge finds Open Watcom in $WATCOM_DIR with:  export WATCOM=$WATCOM_DIR"
fi

# ---- the prefix of Wine for the cases of win16 (tests/retro/tools/win16.sh); Wine without TMPDIR (Wine 9 of Ubuntu frees getenv("TMPDIR"))
if wine32_ok && have xvfb-run && [ ! -f "$WINEPREFIX_RETRO/system.reg" ]; then
    say "prefix of Wine: $WINEPREFIX_RETRO"
    mkdir -p "$WINEPREFIX_RETRO"
    env -u TMPDIR WINEDEBUG=-all WINEPREFIX="$WINEPREFIX_RETRO" xvfb-run -a wineboot -i > /dev/null 2>&1
    env -u TMPDIR WINEPREFIX="$WINEPREFIX_RETRO" wineserver -w 2> /dev/null
fi

# ---- FreeDOS 1.4 (optional): the cases with a 64-bit processor run it under QEMU (DOSBox-X has no long mode)
if [ $freedos -eq 1 ] && [ ! -f "$CACHE/freedos/FD14LITE.img" ]; then
    say "FreeDOS 1.4: $FREEDOS_URL -> $CACHE/freedos/FD14LITE.img"
    mkdir -p "$CACHE/freedos"
    tmp2=$(mktemp -d)
    curl -fL --retry 3 -o "$tmp2/fd.zip" "$FREEDOS_URL" && unzip -o -q "$tmp2/fd.zip" FD14LITE.img -d "$CACHE/freedos" || echo "FreeDOS: download or unpacking failed"
    rm -rf "$tmp2"
fi

# ---- forge-c of this checkout, laid out like a distribution (native/forge/package-dist.sh makes a full one, with the VM and Prebuilt/)
if [ -n "$forgedir" ]; then
    say "forge-c: $REPO/native/forge/forge.c -> $forgedir/forge-c"
    mkdir -p "$forgedir" || exit 1
    forgedir=$(cd "$forgedir" && pwd)
    ${CC:-cc} -O2 "$REPO/native/forge/forge.c" -o "$forgedir/forge-c.partial" -lm -lpthread > "$forgedir/build.log" 2>&1 \
        || { echo "building forge-c failed:"; tail -20 "$forgedir/build.log"; exit 1; }
    mv "$forgedir/forge-c.partial" "$forgedir/forge-c"
    ln -sfn "$REPO/native" "$forgedir/native"
    ln -sfn "$REPO/packages" "$forgedir/Source"
    say "the standard packages -> $forgedir/Packages (a minute or two)"
    (cd "$forgedir" && env -u FIRE_TABLES -u FIRE_PACKAGES -u FIRE_NATIVE_ROOT ./forge-c install-standard) > "$forgedir/install.log" 2>&1 \
        || { echo "install-standard failed:"; tail -20 "$forgedir/install.log"; exit 1; }
    echo "  then: $forgedir/forge-c conformance retro -j 4   (in $REPO)"
fi

echo
report
