# oscarwatch_ft8

Thin C wrapper around [ft8_lib](https://github.com/kgoba/ft8_lib) (MIT) for OscarWatch FT4 encode/decode.

## Output locations

| Platform | RID | File |
|----------|-----|------|
| Windows x64 | `win-x64` | `OscarWatch/runtimes/win-x64/native/oscarwatch_ft8.dll` |
| Windows ARM64 | `win-arm64` | `OscarWatch/runtimes/win-arm64/native/oscarwatch_ft8.dll` |
| macOS Apple Silicon | `osx-arm64` | `OscarWatch/runtimes/osx-arm64/native/oscarwatch_ft8.dylib` |
| macOS Intel | `osx-x64` | `OscarWatch/runtimes/osx-x64/native/oscarwatch_ft8.dylib` |
| Linux x64 | `linux-x64` | `OscarWatch/runtimes/linux-x64/native/oscarwatch_ft8.so` |
| Linux ARM64 | `linux-arm64` | `OscarWatch/runtimes/linux-arm64/native/oscarwatch_ft8.so` |

The managed loader (`Ft8Native`) looks under `runtimes/<rid>/native/` for the matching file name.

## Build (this machine)

On this Windows PC the native library is built in WSL with the MinGW cross compiler, not with the Windows `cmake` or MSVC.

```bash
cmake -S native/oscarwatch_ft8 -B native/oscarwatch_ft8/build-win-x64 \
  -DCMAKE_SYSTEM_NAME=Windows \
  -DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc
cmake --build native/oscarwatch_ft8/build-win-x64 --config Release
mkdir -p OscarWatch/runtimes/win-x64/native
cp native/oscarwatch_ft8/build-win-x64/oscarwatch_ft8.dll \
  OscarWatch/runtimes/win-x64/native/
```

Publish CI builds the library for each RID before `dotnet publish` so release packages include the native binary. Linux jobs stay on Ubuntu 24.04, and the package check rejects a library that needs glibc newer than 2.35, so Linux Mint and Raspberry Pi OS can load it.
