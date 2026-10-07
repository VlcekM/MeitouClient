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

`src/Meitou.Rendering/Upscalers/FsrUpscaler.cs` declares the FidelityFX API's type constants, flags and
structures following the SDK's public headers (`ffx_api.h`, `ffx_api_types.h`, `ffx_upscale.h`, SDK v1.1.4). The
FidelityFX library itself (`amd_fidelityfx_vk.dll`) is not part of this repository; it is loaded at run time when the
user provides it.

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

`src/Meitou.Rendering/Upscalers/Streamline.cs` and `DlssUpscaler.cs` declare Streamline's structures, enums and
entry points following its public headers (Streamline 2.14.1). The Streamline libraries and NVIDIA's DLSS runtime are
not part of this repository; they are loaded at run time when the user provides them (see
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

## NuGet packages

Used as packages (not copied into this repository); each is distributed under its own license.

| Package | Used by | License |
| --- | --- | --- |
| StbImageSharp 2.30.16 | `Meitou.Data` (PNG/TGA/JPG/BMP textures) | Unlicense OR MIT |
| Silk.NET.Windowing, .Input 2.23.0 (and their Silk.NET dependencies: Core, Maths, GLFW, Input/Windowing Common and Glfw) | `src/Meitou.Rendering.Display`, `src/Meitou.Game`, `tools/Meitou.ModelViewer` | MIT |
| Ultz.Native.GLFW 3.4.0 (ships `glfw3.dll`) | `tools/Meitou.ModelViewer`, via Silk.NET | Zlib |
| Silk.NET.Vulkan, .Vulkan.Extensions.KHR, .Vulkan.Extensions.EXT 2.23.0 | `src/Meitou.Rendering` | MIT |
| Silk.NET.Shaderc 2.23.0 | `src/Meitou.Rendering` (GLSL to SPIR-V at run time) | MIT |
| Silk.NET.Shaderc.Native 2.23.0 (ships `shaderc_shared.dll`: Google's shaderc with glslang and SPIRV-Tools) | `src/Meitou.Rendering` | Apache-2.0 (shaderc, SPIRV-Tools); glslang under its own BSD-3-Clause / MIT-style terms |
| StbTrueTypeSharp 1.26.12 | `tools/Meitou.ModelViewer` (key-list overlay text, from a system monospace font; no font is shipped) | Public domain |

The DDS reader and decoder (`src/Meitou.Data/Textures/`) are written from Microsoft's public DDS and
block-compression documentation; no code is taken from other implementations.
