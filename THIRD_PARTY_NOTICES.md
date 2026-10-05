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

## NuGet packages

Used as packages (not copied into this repository); each is distributed under its own license.

| Package | Used by | License |
| --- | --- | --- |
| StbImageSharp 2.30.16 | `Meitou.Data` (PNG/TGA/JPG/BMP textures) | Unlicense OR MIT |
| Silk.NET.Windowing, .Input 2.23.0 (and their Silk.NET dependencies: Core, Maths, GLFW, Input/Windowing Common and Glfw) | `src/Meitou.Rendering.Display`, `src/Meitou.Game`, `tools/Meitou.ModelViewer` | MIT |
| Ultz.Native.GLFW 3.4.0 (ships `glfw3.dll`) | `tools/Meitou.ModelViewer`, via Silk.NET | Zlib |
| StbTrueTypeSharp 1.26.12 | `tools/Meitou.ModelViewer` (key-list overlay text, from a system monospace font; no font is shipped) | Public domain |

The DDS reader and decoder (`src/Meitou.Data/Textures/`) are written from Microsoft's public DDS and
block-compression documentation; no code is taken from other implementations.
