using System.Numerics;
using Meitou.Content;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Ogre;

public class OgreMaterialTests
{
    static OgreScriptFile Compile(string text, Dictionary<string, string>? files = null) =>
        OgreScriptCompiler.Compile(text, "test.material", name => files?.GetValueOrDefault(name));

    static OgreMaterialLibrary Library(string text, Dictionary<string, string>? files = null)
    {
        var library = new OgreMaterialLibrary();
        library.AddScript(text, "test.material", name => files?.GetValueOrDefault(name));
        return library;
    }

    // ------------------------------------------------------------------ inheritance

    const string Base = """
        material Base
        {
            receive_shadows off
            technique
            {
                pass main
                {
                    lighting off
                    diffuse 1 0 0
                    texture_unit diffuseMap { texture a.dds }
                    texture_unit normalMap { texture n.dds }
                }
                pass
                {
                    depth_write off
                }
            }
        }
        """;

    [Fact]
    public void Derived_material_overlays_base_and_its_own_properties_win()
    {
        var m = Library(Base + """

            material Derived : Base
            {
                technique
                {
                    pass main
                    {
                        diffuse 0 1 0
                        texture_unit normalMap { texture n2.dds }
                        texture_unit extra { texture e.dds }
                    }
                }
            }
            """).Find("Derived")!;

        Assert.False(m.ReceiveShadows);
        var t = Assert.Single(m.Techniques);
        Assert.Equal(2, t.Passes.Count);
        var main = t.Passes[0];
        Assert.Equal("main", main.Name);
        Assert.False(main.Lighting);
        Assert.Equal(new Vector4(0, 1, 0, 1), main.Diffuse);

        // Base properties come first, so the raw list still has both diffuse lines.
        Assert.Equal(["lighting off", "diffuse 1 0 0", "diffuse 0 1 0"], main.Source.Properties.Select(p => p.ToString()));

        // Matched by name and overlaid; the unmatched base unit is inserted before; the new one stays last.
        Assert.Equal(["diffuseMap a.dds", "normalMap n2.dds", "extra e.dds"], main.TextureUnits.Select(u => $"{u.Name} {u.Texture}"));
        Assert.Equal(2, main.TextureUnits[1].Source.Properties.Count(p => p.Name == "texture"));
        Assert.False(t.Passes[1].DepthWrite);
    }

    [Fact]
    public void Imports_bring_bases_from_other_files()
    {
        var files = new Dictionary<string, string>
        {
            ["base.material"] = Base + "\nmaterial Second { technique { pass { lighting off } } }",
        };
        var all = Library("import * from \"base.material\"\nmaterial A : Base Second { }", files);
        Assert.Empty(all.Diagnostics);

        // Imported objects are only bases: they are not defined by the importing file.
        Assert.Equal(["A"], all.Materials.Keys);

        // Base's technique is copied in; Second's unnamed technique then pairs with that copy by position, and its
        // unnamed pass with the copy's first pass ("main": a named object can take an unnamed base by position).
        var a = Assert.Single(all.Find("A")!.Techniques);
        Assert.Equal(["main", ""], a.Passes.Select(p => p.Name));
        Assert.Equal(2, a.Passes[0].Source.Properties.Count(p => p.Name == "lighting"));

        var one = Library("import Base from base.material\nmaterial A : Base { }\nmaterial B : Second { }", files);
        Assert.Equal(OgreScriptError.ObjectBaseNotFound, Assert.Single(one.Diagnostics).Code);
        Assert.Equal(2, one.Find("A")!.Techniques[0].Passes.Count);

        var missing = Library("import * from nothere.material\nmaterial A : Base { }");
        Assert.Equal([OgreScriptError.ImportNotFound, OgreScriptError.ObjectBaseNotFound], missing.Diagnostics.Select(d => d.Code));

        // Ogre only searches the file and its imports, never other files.
        Assert.Empty(missing.Find("A")!.Techniques);
    }

    [Fact]
    public void Variables_expand_from_object_scope_bases_and_globals()
    {
        var library = Library("""
            set $blend add
            set $blend alpha_blend
            material Base
            {
                set $colour "0.25 0.5 1"
                technique
                {
                    pass
                    {
                        diffuse $colour
                        scene_blend $blend
                    }
                }
            }
            material Derived : Base
            {
                set $colour "1 1 0 0.5"
            }
            material Undefined { technique { pass { ambient $nope } } }
            """);
        var basePass = library.Find("Base")!.Techniques[0].Passes[0];
        Assert.Equal(new Vector4(0.25f, 0.5f, 1, 1), basePass.Diffuse);

        // The first top-level set wins (std::map insert).
        Assert.Equal(new OgreSceneBlend(OgreSceneBlendFactor.One, OgreSceneBlendFactor.One), basePass.SceneBlend);
        Assert.Equal(new Vector4(1, 1, 0, 0.5f), library.Find("Derived")!.Techniques[0].Passes[0].Diffuse);
        Assert.Equal(OgreScriptError.UndefinedVariable, Assert.Single(library.Diagnostics).Code);
    }

    // ------------------------------------------------------------------ typed accessors

    [Fact]
    public void Texture_units_resolve_aliases_and_list_their_files()
    {
        var m = Library("""
            material M
            {
                set_texture_alias Main brick.dds
                set_texture_alias other_alias other.dds
                technique
                {
                    pass
                    {
                        texture_unit Main { texture placeholder.dds }
                        texture_unit unit2 { texture_alias other_alias }
                        texture_unit gbuffer { content_type compositor global_gbuffer 2 }
                        texture_unit sky { cubic_texture sky.dds separateUV }
                        texture_unit cube { cubic_texture env.dds combinedUVW }
                        texture_unit anim { anim_texture flame.png 3 1.5 }
                        texture_unit "quoted unit" { texture "a b.dds" 2d gamma }
                    }
                }
            }
            """).Find("M")!;
        var u = m.Techniques[0].Passes[0].TextureUnits;
        Assert.Equal("brick.dds", u[0].Texture);
        Assert.Equal("other.dds", u[1].Texture);
        Assert.Empty(u[2].TextureFiles());
        Assert.Equal(["compositor", "global_gbuffer", "2"], u[2].ContentType!);
        Assert.Equal(["sky_fr.dds", "sky_bk.dds", "sky_lf.dds", "sky_rt.dds", "sky_up.dds", "sky_dn.dds"], u[3].TextureFiles());
        Assert.Equal(["env.dds"], u[4].TextureFiles());
        Assert.Equal(["flame_0.png", "flame_1.png", "flame_2.png"], u[5].TextureFiles());
        Assert.Equal("quoted unit", u[6].Name);
        Assert.Equal("a b.dds", u[6].Texture);
        Assert.True(u[6].Gamma);
    }

    // ------------------------------------------------------------------ base game

    static GameInstall Install()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        return install!;
    }

    /// <summary>
    /// Every .program and .material under data/ compiles. The only diagnostics are repeated names: 10 materials
    /// left over from model exports (01-Default in 7 files, NoMaterial in 3, Standard_3 and Material#131 in 2 each)
    /// and 10 programs: items/armour/meshes/Shaders.program repeats the 9 of character/meshes/Shaders.program, and
    /// materials/post/atmospherefog.material defines Deferred_VP_HLSL again (deferred.material has it first).
    /// Every base object and every referenced GPU program is found.
    /// </summary>
    [Fact]
    [Slow]
    public void Compiles_every_base_game_material_script()
    {
        var install = Install();
        var library = OgreMaterialLibrary.LoadAll(install, out _);

        var materialFiles = Directory.EnumerateFiles(install.DataDirectory, "*.material", SearchOption.AllDirectories).Count();
        Assert.Equal(materialFiles, library.Files.Count(f => f.Name.EndsWith(".material", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal([OgreScriptError.DuplicateDefinition], library.Diagnostics.Select(d => d.Code).Distinct());
        Assert.Equal(10, library.DuplicateMaterials.Count);
        var programs = library.Diagnostics.Where(d => !d.Message.StartsWith("material", StringComparison.Ordinal)).ToList();
        Assert.Equal(9, programs.Count(d => d.File == Path.Combine("items", "armour", "meshes", "Shaders.program")));
        Assert.Equal("vertex_program Deferred_VP_HLSL",
            Assert.Single(programs, d => d.File.EndsWith("atmospherefog.material", StringComparison.Ordinal)).Message.Split(',')[0]);
        Assert.Equal(10, programs.Count);

        var passes = library.Materials.Values.SelectMany(m => m.Techniques).SelectMany(t => t.Passes).ToList();
        Assert.All(passes.SelectMany(p => new[] { p.VertexProgram, p.FragmentProgram }).OfType<OgreProgramRef>(),
            r => Assert.True(library.Programs.ContainsKey(r.Name), r.ToString()));

        // Building_Dual : StaticObject (buildings.material, via import * from "objects.material") adds a vertex and
        // fragment program and three texture units after the three inherited ones.
        var dual = library.Find("Building_Dual")!.Techniques[0];
        Assert.Equal("standardShadowCaster", dual.ShadowCasterMaterial);
        var pass = Assert.Single(dual.Passes);
        Assert.Equal("Object_Coloured_VP", pass.VertexProgram!.Name);
        Assert.Equal("Object_Dual_FP", pass.FragmentProgram!.Name);
        Assert.Equal(["diffuseMap", "normalMap", "metalnessMap", "diffuseMap2", "normalMap2", "metalnessMap2"], pass.TextureUnits.Select(u => u.Name));
        Assert.Equal(["black.dds", "flat.dds", "black.dds", null, null, "black.dds"], pass.TextureUnits.Select(u => u.Texture));
    }
}
