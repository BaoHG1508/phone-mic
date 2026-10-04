#!/usr/bin/env bash
# Builds dist/windows/PhoneMicReceiver.exe (self-contained, single file, win-x64) from macOS/Linux/Windows.
# When cross-building from a non-Windows host the .NET SDK cannot customise the app host, so the
# executable comes out flagged as a *console* program; the Python step below flips the PE subsystem
# to GUI so no console window appears behind the app. (Building on Windows does not need that step.)
set -euo pipefail
cd "$(dirname "$0")"
dotnet test src/PhoneMic.Core.Tests -v quiet
dotnet publish src/PhoneMicReceiver/PhoneMicReceiver.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:EnableWindowsTargeting=true -o ../dist/windows
python3 - <<'PY'
import struct, pathlib
p = pathlib.Path("../dist/windows/PhoneMicReceiver.exe")
d = bytearray(p.read_bytes())
pe = struct.unpack_from("<I", d, 0x3C)[0]
assert d[pe:pe+4] == b"PE\0\0"
off = pe + 24 + 68  # IMAGE_OPTIONAL_HEADER.Subsystem
if struct.unpack_from("<H", d, off)[0] == 3:
    struct.pack_into("<H", d, off, 2)
    p.write_bytes(d)
    print("patched PE subsystem: console -> GUI")
else:
    print("PE subsystem already GUI")
PY
ls -la ../dist/windows/PhoneMicReceiver.exe
