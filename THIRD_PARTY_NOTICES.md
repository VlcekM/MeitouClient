# Third-party notices

MeitouClient is licensed under GPL-3.0-or-later (see [LICENSE](LICENSE)). Parts of it are derived
from the following projects, whose licenses require these notices.

## OGRE

`src/Meitou.Data/Ogre/` reads Ogre file formats following the structure of OGRE's serializers
(`OgreMeshSerializerImpl.cpp`, `OgreSkeletonSerializer.cpp`) and scripts following its script
lexer, parser and compiler (`OgreScriptLexer.cpp`, `OgreScriptParser.cpp`, `OgreScriptCompiler.cpp`),
ogre-next branch `v2-0`.

```
OGRE (www.ogre3d.org) is made available under the MIT License.

Copyright (c) 2000-2013 Torus Knot Software Ltd

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## AMD FidelityFX SDK

`src/Meitou.Game/Rendering/Upscalers/FsrUpscaler.cs` declares the FidelityFX API's type constants, flags and
structures following the SDK's public headers (`ffx_api.h`, `ffx_api_types.h`, `ffx_upscale.h`, SDK v1.1.4). The
FidelityFX library itself (`amd_fidelityfx_vk.dll`, or `libamd_fidelityfx_vk.so`) is not part of this repository; it is
loaded at run time when the user provides it. The Linux `libamd_fidelityfx_vk.so` is an unofficial build of SDK 1.1.4
modified for Linux (built from AMD's MIT-licensed source at tag v1.1.4 with `tools/patches/fidelityfx-sdk-1.1.4-linux.patch` by
`tools/scripts/build-ffx-linux.sh`), not provided by AMD; the licence text
travels with it as `LICENSE-FidelityFX.txt`. The Linux release zip includes that build, the Windows zip AMD's signed prebuilt DLL;
both carry AMD's licence in `licenses/AMD`.

```
Copyright (C) 2024 Advanced Micro Devices, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files(the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and /or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions :

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## NVIDIA Streamline

`src/Meitou.Game/Rendering/Upscalers/Streamline.cs` and `DlssUpscaler.cs` declare Streamline's structures, enums and
entry points following its public headers (Streamline 2.14.16). The Streamline libraries and NVIDIA's DLSS runtime
(`nvngx_dlss.dll`) are not part of this repository; they are included in the Windows release zip under NVIDIA's
licences (`licenses/NVIDIA`), and a build from source loads them at run time when the user provides them (see
[LICENSE-EXCEPTION.md](LICENSE-EXCEPTION.md) for the DLSS runtime).

```
Copyright (c) 2023 NVIDIA CORPORATION. All rights reserved

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## NVIDIA DLSS (NGX)

`src/Meitou.Game/Rendering/Upscalers/Ngx.cs` and `NgxDlssUpscaler.cs` declare, in our own code, the interface needed to call
NGX's documented Vulkan API on Linux: entry point signatures, structure layouts and parameter names. The interface
identifiers (entry point and parameter names, constants such as the API version) are restated from NVIDIA's DLSS SDK
headers (NVIDIA RTX SDKs license); no other header text and no SDK file is part of this repository. That API comes from
NVIDIA's NGX client library, the DLSS SDK's static `libnvsdk_ngx.a`, which `tools/scripts/build-ngx-shim.sh` downloads
(SHA-256 checked) and links unchanged into `libmeitou_ngx.so`. It and the DLSS runtime (`libnvidia-ngx-dlss.so.*`), both
NVIDIA DLSS SDK 310.9.16, are included in the Linux release zip under NVIDIA's licence (`licenses/NVIDIA`); a build from
source loads them at run time when the user provides them (see [LICENSE-EXCEPTION.md](LICENSE-EXCEPTION.md)).
On Windows (and under Proton), with frame generation on, DLSS goes through the same client library instead of Streamline: its
static `nvsdk_ngx_s.lib` (`lib/Windows_x86_64/x64/`, same SDK version) is downloaded by `tools/scripts/build-ngx-shim-win.sh`
(SHA-256 checked) and linked unchanged into `meitou_ngx.dll`, under the same NVIDIA licence and conditions; the script links it
against the Microsoft C runtime and Windows SDK libraries fetched by `xwin` (Microsoft's licence; the static C runtime ends up inside the DLL, the
libraries themselves are not redistributed). The Windows release zip includes `meitou_ngx.dll`, linked the same way with Visual Studio's linker
by `tools/scripts/package-release.ps1`.

## NuGet packages

Used as packages (not copied into this repository); each is distributed under its own license.

| Package | Used by | License |
| --- | --- | --- |
| StbImageSharp 2.30.16 | `Meitou.Data` (PNG/TGA/JPG/BMP textures) | Unlicense OR MIT |
| Silk.NET.Windowing, .Input 2.23.0 (and their Silk.NET dependencies: Core, Maths, GLFW, Input/Windowing Common and Glfw) | `src/Meitou.Game/Rendering/Display`, `src/Meitou.Game`, `tools/Meitou.ModelViewer` | MIT |
| Ultz.Native.GLFW 3.4.0 (ships `glfw3.dll`) | `tools/Meitou.ModelViewer`, via Silk.NET | Zlib |
| Silk.NET.Vulkan, .Vulkan.Extensions.KHR, .Vulkan.Extensions.EXT 2.23.0 | `src/Meitou.Game/Rendering` | MIT |
| Silk.NET.Shaderc 2.23.0 | `src/Meitou.Game/Rendering` (GLSL to SPIR-V at run time) | MIT |
| Silk.NET.Shaderc.Native 2.23.0 (ships `shaderc_shared.dll`: Google's shaderc with glslang and SPIRV-Tools) | `src/Meitou.Game/Rendering` | Apache-2.0 (shaderc, SPIRV-Tools); glslang under its own BSD-3-Clause / MIT-style terms |
| StbTrueTypeSharp 1.26.12 | `src/Meitou.Game/Rendering` (debug overlay text from a system monospace font, else the installed game's Exo2 SemiBold, kerning pairs of UI fonts; no font is shipped) | Public domain |
| FreeTypeSharp 3.1.0 (ships `freetype.dll`, `libfreetype.so`, `libfreetype.dylib`: FreeType 2.13.2) | `src/Meitou.Game/Rendering` (UI font rasterisation and hinting). Portions of this software are copyright The FreeType Project (www.freetype.org). All rights reserved. | MIT (FreeTypeSharp); FreeType License (FTL) or GPLv2, used here under the FTL |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent 12.1.4 (with SkiaSharp and HarfBuzzSharp) | `launcher/Meitou.Launcher` (UI) | MIT |
| Velopack 1.2.161 | `launcher/Meitou.Launcher` (install and self-update) | MIT |
| Markdig 1.4.2 | `launcher/Meitou.Launcher` (news posts) | BSD-2-Clause |
| NVorbis 0.10.5 | `src/Meitou.Data`, `src/Meitou.Game/Audio`, `launcher/Meitou.Launcher` (news video sound) | MIT |
| Silk.NET.OpenAL.Soft.Native 1.23.1 (ships OpenAL Soft: `soft_oal.dll`, `libopenal.so`, `libopenal.dylib`) | `src/Meitou.Game/Audio`, `launcher/Meitou.Launcher` (news video sound; a separate library, loaded at run time) | LGPL-2.1-or-later (OpenAL Soft) |
| Lib.Harmony 2.4.3 (ships `0Harmony.dll` with MonoMod, Mono.Cecil and Iced merged in; notices under [Harmony](#harmony)) | `src/Meitou.Game` (Mods/; code mods patch the game through it) | MIT |

The DDS reader and decoder (`src/Meitou.Data/Textures/`) are written from Microsoft's public DDS and
block-compression documentation; no code is taken from other implementations.

## Harmony

`0Harmony.dll` (NuGet `Lib.Harmony` 2.4.3) ships with the game for code mods (docs/modding.md). It merges MonoMod, Mono.Cecil and Iced, whose
notices follow Harmony's.

```
MIT License

Copyright (c) 2017 Andreas Pardeike

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
```

```
The MIT License (MIT)

Copyright (c) 2015 - 2020 0x0ade

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
```

```
Copyright (c) 2008 - 2015 Jb Evain
Copyright (c) 2008 - 2011 Novell, Inc.

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

```
https://github.com/icedland/iced
Copyright (C) 2018-present iced project and contributors

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## libvpx

libvpx 1.16.0, built from source by vcpkg and linked into the published launcher (news videos, `launcher/Meitou.Launcher/VpxDecoder.cs`).
BSD-3-Clause, with Google's patent grant (`PATENTS` in the libvpx source); the x86 builds also contain its `third_party/x86inc` macros (ISC).

```
Copyright (c) 2010, The WebM Project authors. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of Google, nor the WebM Project, nor the names
    of its contributors may be used to endorse or promote products
    derived from this software without specific prior written
    permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## DotRecast

`src/Meitou.Simulation/Navigation/` uses the DotRecast NuGet packages `DotRecast.Core` and `DotRecast.Recast`
(version 2026.3.1) for voxelisation and polygon-mesh generation; it is a port of Recast Navigation.

```
Copyright (c) 2024 Choi Ikpil ikpil@naver.com
Copyright (c) 2009-2010 Mikko Mononen memon@inside.org

This software is provided 'as-is', without any express or implied
warranty. In no event will the authors be held liable for any damages
arising from the use of this software.
Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:
1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

## Kenshi logo

`assets/branding/kenshi-logo.png` (shown on the Meitou title screen) is the Kenshi logo, artwork
and trademark of Lo-Fi Games. It is not covered by this project's GPL license and is included
only to identify the game this engine runs. MeitouClient is not affiliated with or endorsed by
Lo-Fi Games.
