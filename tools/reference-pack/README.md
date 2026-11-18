# Meitou.References

Compile code mods for [MeitouClient](https://github.com/VlcekM/MeitouClient) without a game install. The package version is the game
release it was built from.

```xml
<PackageReference Include="Meitou.References" Version="<game version>" />
```

The mod's project targets `net11.0` (standard builds) or `net10.0` (legacy macOS builds) and gets references to `MeitouClient`,
`Meitou.Simulation`, `Meitou.Core`, `Meitou.Data`, `0Harmony` and the game's other libraries (Bepu, DotRecast, Silk.NET and the rest).
Nothing is copied to the mod's output: the game supplies the real assemblies when it loads the mod. The Meitou assemblies here are
compile-only reference assemblies that .NET refuses to run.

## Internals

```xml
<PropertyGroup>
  <MeitouPublicized>true</MeitouPublicized>
</PropertyGroup>
```

The mod then compiles against copies in which every type and member is public, and gets `IgnoresAccessChecksTo` for the four Meitou
assemblies, which lets it reach those members at run time. Internals change between releases without notice.

## Licences

The Meitou assemblies are built from MeitouClient's source (GPL-3.0-or-later, `LICENSE.txt`). The other libraries are under their own
licences, listed in `THIRD_PARTY_NOTICES.md`; `licenses/Apache-2.0.txt` is the text for BepuPhysics and BepuUtilities.

The guide: [docs/modding.md](https://github.com/VlcekM/MeitouClient/blob/master/docs/modding.md).
