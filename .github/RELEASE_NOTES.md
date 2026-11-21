# OscarWatch v1.1.8-OQ4

This release updates the OQ4 fork with upstream OscarWatch changes and keeps the experimental short-packet OQ4 mode available alongside FT4 and FT8.

## What's new

- **FT8 for QO-100:** open the new FT8 window from Tools when an appropriate transponder is selected. FT4 and OQ4 remain available.
- **OQ4 window:** the station setup banner now correctly identifies OQ4. The existing basic and advanced activity layouts, waterfall controls and targeted decode-again feature remain available.
- **Radio and PTT integration:** retains OQ4's transmitter ownership safeguards while incorporating upstream's shared serial-handshake port support and FT4/FT8 updates.
- **Satellite information:** updated satellite names and transponder database support, operating notices, and warnings for stale orbital elements.
- **Logbook:** ADIF import and export improvements, WSJT-X log broadcasts, activation references, and additional Cloudlog options.
- **Other upstream changes:** live CW decoding and logbook keying, SSTV improvements, radio-control fixes and the OscarWatch Troubleshooter. See the [upstream 1.1.8 release notes](https://github.com/magicbug/OscarWatch-Tracker/releases/tag/v1.1.8) for details.

## OQ4 testing

OQ4 is experimental. Its earlier decoder processing optimization remains included; a recording replay after the upstream merge recovered all expected messages. The merged application and OQ4, FT4 and FT8 windows were checked locally, and the automated test suite passed. Radio/PTT operation and live audio capture were not tested during that GUI check.

FT4 and OQ4 use different waveforms, so an OQ4 transmission requires an OQ4 receiver. More real-world testing is needed to establish OQ4's performance relative to FT4.

## Downloads

Choose the package for your operating system and processor below. Most Intel and AMD Windows computers use **win-x64**; Windows on ARM uses **win-arm64**. Linux ARM64 is suitable for 64-bit Raspberry Pi systems.

Thank you to Peter Goodhall and the OscarWatch contributors, the FT4/FT8 authors, and everyone helping test OQ4.
