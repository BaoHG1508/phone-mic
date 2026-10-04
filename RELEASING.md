# Releasing

Binaries are attached to GitHub Releases, not committed to the repository.

## 1. Android APK

The release build is signed with a keystore kept **outside** this repository. The path and
passwords live in `~/.phonemic-release/keystore.properties`, which `android/app/build.gradle.kts`
reads when present; without it the release build falls back to the debug signing key.

Create the keystore once:

```bash
mkdir -p ~/.phonemic-release
keytool -genkeypair -v -keystore ~/.phonemic-release/phonemic-release.jks \
  -alias phonemic -keyalg RSA -keysize 4096 -validity 10000
```

Then write `~/.phonemic-release/keystore.properties` with `storeFile`, `storePassword`,
`keyAlias` and `keyPassword`. Back that folder up: losing the key means future versions cannot
update an already installed app.

Build:

```bash
cd android && ./gradlew :app:assembleRelease
```

Output: `android/app/build/outputs/apk/release/app-release.apk`.

## 2. Windows receiver

```bash
cd pc-receiver-windows && ./publish.sh
```

Output: `dist/windows/PhoneMicReceiver.exe`, a self-contained single file for win-x64.
Building on macOS or Linux also flips the PE subsystem from console to GUI; see the script.

The executable is unsigned, so Windows SmartScreen shows a warning on first run. Signing it
needs a code-signing certificate.

## 3. Publish

```bash
git tag v1.0.0 && git push origin v1.0.0
gh release create v1.0.0 \
  "android/app/build/outputs/apk/release/app-release.apk#PhoneMic-1.0.0.apk" \
  "dist/windows/PhoneMicReceiver.exe#PhoneMicReceiver-1.0.0.exe" \
  --title "PhoneMic 1.0.0" --notes-file docs/release-notes-1.0.0.md
```

The `Release artifacts` workflow can rebuild both files on GitHub runners instead: run it manually
from the Actions tab and give it the tag name. It only signs the APK properly if the repository
secrets listed in the workflow are set.
