#!/bin/bash
# Builds Pipework.app and zips it for a GitHub release.
# Usage: ./build-app.sh            (Intel, osx-x64)
#        RID=osx-arm64 ./build-app.sh   (Apple Silicon)
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(grep -o 'AppVersion = "[^"]*"' Views/MainWindow.cs | cut -d'"' -f2)
BUILD=$(git rev-list --count HEAD 2>/dev/null || echo 0)
HASH=$(git rev-parse --short HEAD 2>/dev/null || echo nogit)
RID=${RID:-osx-x64}
ARCH=${RID#osx-}
APP="dist/Pipework.app"
ZIP="dist/Pipework-v${VERSION}-macOS-${ARCH}.zip"

if [ -n "$(git status --porcelain --untracked-files=no 2>/dev/null)" ]; then
  echo "warning: uncommitted changes, so the build number will show a +"
fi

echo "Building Pipework v$VERSION (build $BUILD, $HASH) for $RID"
rm -rf dist publish
mkdir -p dist
dotnet publish -c Release -r "$RID" --self-contained true -o publish --nologo -v quiet

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R publish/* "$APP/Contents/MacOS/"
cp Assets/pipework.icns "$APP/Contents/Resources/"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Pipework</string>
  <key>CFBundleDisplayName</key><string>Pipework</string>
  <key>CFBundleExecutable</key><string>Pipework</string>
  <key>CFBundleIconFile</key><string>pipework</string>
  <key>CFBundleIdentifier</key><string>com.smokeluce.pipework</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$BUILD</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

codesign --force --deep -s - "$APP"
codesign --verify --deep --strict "$APP" && echo "signature ok"
ditto -c -k --keepParent "$APP" "$ZIP"
echo "Built $APP"
echo "Zip:   $ZIP ($(du -h "$ZIP" | cut -f1))"
