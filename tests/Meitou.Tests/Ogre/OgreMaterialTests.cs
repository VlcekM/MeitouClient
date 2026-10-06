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

    static string[] Lexemes(string text) => [.. OgreScriptLexer.Tokenize(text, "t").Select(t => t.Lexeme.Replace("\r", "\\n").Replace("\n", "\\n"))];

    // ------------------------------------------------------------------ lexer

    [Fact]
    public void Lexer_splits_words_braces_and_collapses_newlines()
    {
        Assert.Equal(["material", "a", ":", "b", "{", "\\n", "x", "1", "}"], Lexemes("material a : b {\n\n  x 1}"));
        Assert.Equal(["a", "{", "}"], Lexemes("a{}"));
        // A brace or colon only splits a word after its first character: "}x" stays one word, as in Ogre.
        Assert.Equal(["}x", "x", ":", "y"], Lexemes("}x x: y"));
        Assert.Equal(["x", ":", "y"], Lexemes("x:y"));
    }

    [Fact]
    public void Lexer_comments_start_only_between_tokens()
    {
        Assert.Equal(["a", "\\n", "b"], Lexemes("a // comment\nb"));
        Assert.Equal(["a.dds//not-a-comment"], Lexemes("a.dds//not-a-comment"));
        // ...but a colon ends a word, so in "http://x" the "//" starts a comment.
        Assert.Equal(["http", ":"], Lexemes("http://x"));
        // A block comment swallows its newlines, joining what is around it into one line.
        Assert.Equal(["a", "b", "\\n", "c"], Lexemes("a /* one\ntwo */ b\nc"));
        Assert.Equal(["a", "{"], Lexemes("a /*: base*/ {"));
    }

    [Fact]
    public void Lexer_reads_quotes_variables_and_counts_crlf_lines()
    {
        var tokens = OgreScriptLexer.Tokenize("texture \"a b.dds\"\r\n\r\nx $var \"q\\\"q\"", "t");
        Assert.Equal(OgreScriptTokenType.Quote, tokens[1].Type);
        Assert.Equal("\"a b.dds\"", tokens[1].Lexeme);
        Assert.Equal(1, tokens[1].Line);
        Assert.Equal(3, tokens[3].Line);
        Assert.Equal(OgreScriptTokenType.Variable, tokens[4].Type);
        Assert.Equal("\"q\"q\"", tokens[5].Lexeme);
        var e = Assert.Throws<OgreScriptException>(() => OgreScriptLexer.Tokenize("a\n\"open", "t"));
        Assert.Equal(2, e.Line);
    }

    // ------------------------------------------------------------------ parser and compiler

    [Fact]
    public void Compiles_objects_properties_and_values()
    {
        var file = Compile("""
            vertex_program VP hlsl
            {
                source a.hlsl
                target vs_3_0 vs_4_0
            }
            material "Quoted Name" : "Base" Other { lighting off }
            """);
        var vp = Assert.IsType<OgreScriptObject>(file.Nodes[0]);
        Assert.Equal(("vertex_program", "VP"), (vp.Class, vp.Name));
        Assert.Equal(["hlsl"], vp.Args);
        Assert.Equal(["vs_3_0", "vs_4_0"], vp.Property("target")!.Args);
        Assert.Equal(1, vp.Line);
        var m = Assert.IsType<OgreScriptObject>(file.Nodes[1]);
        Assert.Equal("Quoted Name", m.Name);
        // Ogre keeps the quotes on base names, so a quoted base is never found.
        Assert.Equal(["\"Base\"", "Other"], m.Bases);
        Assert.Equal(["off"], m.Property("lighting")!.Args);
        Assert.Equal(2, file.Diagnostics.Count(d => d.Code == OgreScriptError.ObjectBaseNotFound));
    }

    [Fact]
    public void A_property_runs_to_the_end_of_its_line()
    {
        // Ogre has no property separator: on one line, everything after the first word is its values.
        var pass = Assert.IsType<OgreScriptObject>(Compile("pass { lighting off depth_write off }").Nodes[0]);
        var lighting = Assert.Single(pass.Properties);
        Assert.Equal(["off", "depth_write", "off"], lighting.Args);
        var p = Library("material M { technique { pass { lighting off depth_write off } } }").Find("M")!.Techniques[0].Passes[0];
        Assert.True(p.Lighting); // three values: rejected
        Assert.True(p.DepthWrite);
    }

    [Fact]
    public void Reports_unbalanced_braces()
    {
        Assert.Contains(Compile("material a {\n pass {\n}").Diagnostics, d => d.Code == OgreScriptError.UnclosedBrace);
        Assert.Contains(Compile("material a { }\n}").Diagnostics, d => d.Code == OgreScriptError.UnmatchedBrace);
        Assert.Empty(Compile("material a\n{\n  technique\n  {\n  }\n}").Diagnostics);
    }

    [Fact]
    public void Malformed_import_and_set_throw()
    {
        Assert.Throws<OgreScriptException>(() => Compile("import * from"));
        Assert.Throws<OgreScriptException>(() => Compile("set notavariable 1"));
        Assert.Throws<OgreScriptException>(() => Compile("material a : { }"));
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
    public void Unnamed_objects_match_by_position_and_only_against_unnamed_base_objects()
    {
        var file = Compile(Base + """

            material ByIndex : Base
            {
                technique
                {
                    pass { lighting on }
                    pass { cull_hardware none }
                }
            }
            material NamedAgainstUnnamed : Base
            {
                technique
                {
                    pass other { colour_write off }
                }
            }
            """);
        var library = new OgreMaterialLibrary();
        library.Add(file);

        // Base pass "main" is named, so the derived unnamed passes pair with the base's unnamed pass by position;
        // "main" is copied in front.
        var byIndex = library.Find("ByIndex")!.Techniques[0].Passes;
        Assert.Equal(["main", "", ""], byIndex.Select(p => p.Name));
        Assert.False(byIndex[0].Lighting);
        Assert.True(byIndex[1].Lighting);
        Assert.False(byIndex[1].DepthWrite);
        Assert.Equal(OgreCullHardware.None, byIndex[2].CullHardware);
        Assert.True(byIndex[2].DepthWrite);

        // A named pass that matches no base name still pairs with an unnamed base pass by position.
        var named = library.Find("NamedAgainstUnnamed")!.Techniques[0].Passes;
        Assert.Equal(["main", "other"], named.Select(p => p.Name));
        Assert.False(named[1].DepthWrite);
        Assert.False(named[1].ColourWrite);
    }

    [Fact]
    public void Wildcard_names_apply_to_every_matching_base_object()
    {
        var m = Library(Base + """

            material Wild : Base
            {
                technique { pass main { texture_unit *Map { filtering none } } }
            }
            """).Find("Wild")!;
        var units = m.Techniques[0].Passes[0].TextureUnits;
        Assert.Equal(["diffuseMap", "normalMap"], units.Select(u => u.Name));
        Assert.All(units, u => Assert.Equal(["none"], u.Filtering!));
        Assert.Equal(["a.dds", "n.dds"], units.Select(u => u.Texture));
    }

    [Fact]
    public void Abstract_objects_are_bases_only()
    {
        var library = Library("""
            abstract pass Lit
            {
                lighting off
                diffuse 0.5 0.5 0.5
            }
            abstract material Template { technique { pass : Lit { } } }
            material Real : Template { }
            """);
        Assert.Null(library.Find("Template"));
        var pass = library.Find("Real")!.Techniques[0].Passes[0];
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 1), pass.Diffuse);
        Assert.False(pass.Lighting);
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
    public void Cyclic_imports_do_not_recurse_forever()
    {
        var files = new Dictionary<string, string> { ["a.material"] = "import * from b.material\nmaterial A { }", ["b.material"] = "import * from a.material\nmaterial B { }" };
        var library = Library("import * from a.material\nmaterial C : A B { }", files);
        Assert.NotNull(library.Find("C"));
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
    public void Pass_accessors_follow_the_translator()
    {
        var library = Library("""
            vertex_program VP hlsl
            {
                source v.hlsl
                entry_point main_vs
            }
            material M
            {
                technique
                {
                    scheme Low
                    shadow_caster_material Caster
                    pass
                    {
                        ambient 0.5 0.5 0.5
                        diffuse vertexcolour
                        specular 1 1 1 32
                        emissive 0.1 0.2 0.3 0.4
                        scene_blend one one_minus_src_alpha
                        scene_blend replace
                        depth_write off
                        depth_check off
                        depth_func greater
                        cull_hardware none
                        cull_software front
                        lighting off
                        alpha_rejection greater 128
                        vertex_program_ref Old { }
                        vertex_program_ref VP { param_named_auto m worldviewproj_matrix }
                        fragment_program_ref FP { param_named f float 1 }
                    }
                }
            }
            """);
        var t = library.Find("M")!.Techniques[0];
        Assert.Equal("Low", t.Scheme);
        Assert.Equal("Caster", t.ShadowCasterMaterial);
        var p = t.Passes[0];
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 1), p.Ambient);
        Assert.Equal(Vector4.One, p.Diffuse);
        Assert.Equal(OgreTrackVertexColour.Diffuse, p.VertexColourTracking);
        Assert.Equal(new Vector4(1, 1, 1, 1), p.Specular);
        Assert.Equal(32, p.Shininess);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), p.Emissive);
        // "replace" is rejected by the v2-0 translator, so the earlier value stays.
        Assert.Equal(new OgreSceneBlend(OgreSceneBlendFactor.One, OgreSceneBlendFactor.OneMinusSourceAlpha), p.SceneBlend);
        Assert.False(p.DepthWrite);
        Assert.False(p.DepthCheck);
        Assert.Equal(OgreCompareFunction.Greater, p.DepthFunction);
        Assert.Equal(OgreCullHardware.None, p.CullHardware);
        Assert.Equal(OgreCullSoftware.Front, p.CullSoftware);
        Assert.False(p.Lighting);
        Assert.Equal(new OgreAlphaRejection(OgreCompareFunction.Greater, 128), p.AlphaRejection);
        Assert.Equal("VP", p.VertexProgram!.Name);
        Assert.Equal(["m", "worldviewproj_matrix"], Assert.Single(p.VertexProgram.Parameters).Args);
        Assert.Equal("FP", p.FragmentProgram!.Name);
        Assert.Equal("v.hlsl", library.Programs["VP"].SourceFile);
        Assert.Equal("hlsl", library.Programs["VP"].Language);
    }

    [Fact]
    public void Pass_defaults_match_ogre()
    {
        var p = Library("material M { technique { pass { } } }").Find("M")!.Techniques[0].Passes[0];
        Assert.Equal(Vector4.One, p.Ambient);
        Assert.Equal(Vector4.One, p.Diffuse);
        Assert.Equal(new Vector4(0, 0, 0, 1), p.Specular);
        Assert.Equal(OgreSceneBlend.Replace, p.SceneBlend);
        Assert.True(p.DepthWrite && p.DepthCheck && p.Lighting && p.ColourWrite);
        Assert.Equal(OgreCullHardware.Clockwise, p.CullHardware);
        Assert.Equal(new OgreAlphaRejection(OgreCompareFunction.AlwaysPass, 0), p.AlphaRejection);
        Assert.Null(p.VertexProgram);
    }

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

    [Fact]
    public void First_texture_alias_wins_so_a_derived_material_cannot_replace_a_base_alias()
    {
        var library = Library("""
            material Base
            {
                set_texture_alias Main base.dds
                technique { pass { texture_unit Main { } } }
            }
            material Derived : Base
            {
                set_texture_alias Main derived.dds
                set_texture_alias Other other.dds
            }
            material Twice
            {
                set_texture_alias Main first.dds
                set_texture_alias Main second.dds
                technique { pass { texture_unit Main { } } }
            }
            """);
        Assert.Equal("base.dds", library.Find("Derived")!.Techniques[0].Passes[0].TextureUnits[0].Texture);
        Assert.Equal("first.dds", library.Find("Twice")!.Techniques[0].Passes[0].TextureUnits[0].Texture);
    }

    [Fact]
    public void Invalid_lines_are_ignored_and_the_last_valid_one_wins()
    {
        var p = Library("""
            material M
            {
                technique
                {
                    pass
                    {
                        depth_write off
                        depth_write maybe
                        cull_hardware none
                        cull_hardware sideways
                    }
                }
            }
            """).Find("M")!.Techniques[0].Passes[0];
        Assert.False(p.DepthWrite);
        Assert.Equal(OgreCullHardware.None, p.CullHardware);
    }

    [Fact]
    public void Library_keeps_the_first_definition_and_records_duplicates_and_failures()
    {
        var library = new OgreMaterialLibrary();
        library.AddScript("material A { technique { pass { lighting off } } }", "one.material");
        library.AddScript("material A { }", "two.material");
        library.AddScript("material B { texture \"open", "three.material");
        Assert.False(library.Find("A")!.Techniques[0].Passes[0].Lighting);
        Assert.Equal("two.material", Assert.Single(library.DuplicateMaterials).File);
        Assert.Equal([OgreScriptError.DuplicateDefinition, OgreScriptError.Fatal], library.Diagnostics.Select(d => d.Code));
        Assert.Null(library.Find("B"));
    }

    [Fact]
    [Slow]
    public void Wildcard_match_is_a_glob()
    {
        Assert.True(OgreScriptCompiler.WildcardMatch("diffuseMap", "*Map"));
        Assert.True(OgreScriptCompiler.WildcardMatch("abc", "a*c"));
        Assert.True(OgreScriptCompiler.WildcardMatch("abc", "*"));
        Assert.False(OgreScriptCompiler.WildcardMatch("abc", "*d"));
        Assert.False(OgreScriptCompiler.WildcardMatch("ABC", "a*"));
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
        Assert.Equal("vertex_program Deferred_VP_HLSL", Assert.Single(programs, d => d.File.EndsWith("atmospherefog.material", StringComparison.Ordinal)).Message.Split(',')[0]);
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

    /// <summary>
    /// Mesh material names mostly do not name script materials: Kenshi builds its materials from MATERIAL_SPEC game
    /// records instead (see docs). 12 of the 114 names used by submeshes are defined: StaticObject (1,843 of the
    /// 3,748 submeshes) and 11 exporter default names (01-Default, Material#131...) that happen to have leftover
    /// exported .material files. The undefined ones are exporter defaults and an empty name.
    /// </summary>
    [Fact]
    [Slow]
    public void Base_game_mesh_materials_are_mostly_not_script_materials()
    {
        var install = Install();
        var library = OgreMaterialLibrary.LoadAll(install, out var index);
        var audit = OgreMaterialAudit.Run(install, library, index);

        Assert.Empty(audit.MeshFailures);
        Assert.Equal(0, audit.SubMeshesWithTextureAliases);
        Assert.Equal(114, audit.MeshMaterials.Count);
        Assert.Equal(["01-Default", "02-Default", "08-Default", "19-Default", "Material#131", "Material#26", "Material#63",
            "Material#84", "NoMaterial", "Standard_15thhnth", "Standard_3", "StaticObject"],
            audit.MeshMaterials.Where(library.Materials.ContainsKey));
        Assert.Equal(1843, audit.SubMeshesByMaterial["StaticObject"]);
        Assert.Equal(2400, audit.ResolvedSubMeshes);
        Assert.Equal(3748, audit.SubMeshes);
        Assert.Equal(89, audit.SubMeshesByMaterial[""]);
        Assert.Equal(486, audit.SubMeshesByMaterial["default"]);
    }

    /// <summary>
    /// 124 of the 155 texture names the materials use exist under data/ (80%). The rest are in exporter leftovers
    /// (.psd/.jpg/.bmp names), the Caelum sample materials, names with spaces written unquoted (Ogre reads the
    /// first word only), names with folders, and a few particle textures.
    /// </summary>
    [Fact]
    [Slow]
    public void Base_game_material_textures_mostly_exist()
    {
        var install = Install();
        var library = OgreMaterialLibrary.LoadAll(install, out var index);
        var audit = OgreMaterialAudit.Run(install, library, index);

        Assert.Equal(155, audit.Textures.Count);
        Assert.Equal(124, audit.FoundTextures);
        foreach (var name in new[] { "black.dds", "flat.dds", "white.dds", "nothing.dds", "mp_irradiance.dds" })
            Assert.DoesNotContain(name, audit.MissingTextures.Keys);
        Assert.Contains("Copy", audit.MissingTextures.Keys); // texture Copy of Dplate2.jpg
    }
}
