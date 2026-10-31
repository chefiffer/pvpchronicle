# Third-party notices

The GNU AGPL v3 license in the repository root applies only to original PvP
Chronicle client source and files expressly identified as AGPL-3.0-only. It
does not relicense the materials and components listed below.

## FINAL FANTASY XIV materials

FINAL FANTASY XIV game names, map imagery and game icons included with the
plugin remain the property of Square Enix. Their use is limited to the
non-commercial community purposes and other conditions in the applicable
FINAL FANTASY XIV Materials Usage Policy:
https://support.na.square-enix.com/rule.php?id=5382&la=1&tag=authc

© SQUARE ENIX

This notice is attribution only. It is not a representation that every asset
is authorized for redistribution. Asset provenance and the policy category
covering each asset must be verified before a public release.

## Runtime libraries

The binary package contains the following MIT-licensed runtime libraries:

- `ZstdSharp.dll`, from ZstdSharp.Port 0.8.8, Copyright Oleg Stepanischev
  2026. Source: https://github.com/oleg-st/ZstdSharp
- `System.Security.Cryptography.ProtectedData.dll` 10.0.0, Copyright
  Microsoft Corporation. Source: https://github.com/dotnet/dotnet

The complete MIT license notices distributed with the plugin are in
`licenses/ZstdSharp.Port-MIT.txt` and `licenses/Microsoft-DotNet-MIT.txt`.

## Referenced or adapted implementations

The Frontline and Crystalline Conflict result-packet layout interpretation and
failure-safe hook pattern are independently implemented from public technical
documentation in `wrath16/PvpStats`, whose repository is identified as MIT
licensed. Its current upstream `LICENSE.txt` contains an unfilled copyright
template, so this project does not invent or substitute an upstream copyright
holder. Source: https://github.com/wrath16/PvpStats

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

The combat-event capture model in `CombatEventCapture.cs` is adapted from
Kouzukii's `ffxiv-deathrecap` project:

Copyright (c) 2021 Kouzukii

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is furnished
to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Font

The Thai glyph fallback is Noto Sans Thai, Copyright 2022 The Noto Project
Authors. It is distributed under the SIL Open Font License 1.1. The complete
license is packaged beside the font as `Assets/fonts/OFL-NotoSansThai.txt`.
