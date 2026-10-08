using System.Security.Cryptography;
using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// docs/renderer-native.md 3.4: the legacy model's output must not change while the native model is added. Every world program's legacy
/// SPIR-V (both stages, with and without the clip depth remap) is hashed and compared with the hashes master produced before the native
/// shader text was added (master <c>365bb88</c>). <see cref="GpuApiTests"/> checks LegacyProgram against VkGl in the same build; this checks
/// the build against its past, so a change to shared shader text (or the compiler) that moves a legacy program fails here.
/// </summary>
public class LegacySpirvGoldenTests
{
    /// <summary>SHA-256 of vertex SPIR-V followed by fragment SPIR-V, per world program: without the remap, then with it.</summary>
    static readonly Dictionary<string, (string Plain, string Remap)> Golden = new()
    {
        ["sky simple"] = ("8CFB64AEBF8222327737BB1CBFE25C83", "4FA13EF63843235F949CF456477373F1"),
        ["sky"] = ("EECA10F7D14E9F7D73315F5B375E4D62", "2A88B1820A089F99D4A46EFE6A40D58B"),   // the SkyX cloud pass
        ["water"] = ("30597048B9507F978C7876DA6A23C136", "A181233BB70F3C8DD6B429C146BC4540"),
        ["water meitou"] = ("51EDD7B5F6F19775CBD4AEE71C582FB6", "CE5490A3071BB7F62A3987F9F8A616AF"),   // the Meitou water (2026-10-08)
        ["debug overlay"] = ("2967439E6B38320ED5326B2EF7EA984C", "FFCCCC151047230FD630483A92FEA9E2"),
        ["terrain patch"] = ("A6DF80FA9A18D020192B1ACCFEE17BCF", "BF9958A4D5349310D94BF29727C73D98"),   // the layer skip (0 px); the sandbox grid (off outside --sandbox)
        ["terrain mesh"] = ("310A6B5D645A0DB11BE4F1121BBB5844", "EC83766047C0E1B19020BD055EB6EBD1"),   // the rock crossfade dither (docs/impostors.md section 13; 0 px in Faithful)
        ["terrain patch depth"] = ("3F75081D1AD1F9384233BA473F9CB880", "2059EF6FA02D6613FE9E36BFFC10F05B"),
        ["terrain mesh depth"] = ("8B86D769A1F25EE902EB1B02FB188E92", "A05EBA696FB834240023646D8D10EE21"),
        ["foliage mesh"] = ("7A4408DEAB931EC01A92AD7E395A68BD", "2D76FC2850B6F1D0EC1EE75852B67B65"),
        ["foliage grass"] = ("E2E885CA5C0F1098704E5F327BD8AE12", "61867DAB0A90FDB0C78EFBFF4461A1CB"),
        ["foliage grass motion"] = ("1669CBA644B8869EDE4844E9A0310B5A", "88D5753B3E90F2A3CF4EE314A2B816CF"),
        ["foliage depth"] = ("C9DB0731B3DF7A4ED5BA5686276F6D12", "096B3A079E5982455F14B70F52FE8225"),
        ["buildings"] = ("DDF35A9F194B8F0FF71241364B1E987C", "14AB534CFA0CB49096CD32FCABED60E9"),
        ["buildings depth"] = ("A54FFE418E48878E060098EDA796A99F", "066A9B73CA09D9E7E910F72969FFA788"),
        ["shadow debug"] = ("985BF8FE04970F68BF9DAA974987D5BF", "F0BF97A0C58CE082006B12D436BB9A21"),
        ["shadow atlas"] = ("7A8BC32D82FC65DBE0B7F1BEE4AFB43A", "7C74B349400CB8F9819E044E7F6ED1BD"),
        ["shadow blocker"] = ("6D835FC54063D35442C133C38B55AB73", "C95F868A8A5DC350FE5803D310FF9F8A"),
        ["terrain shadow sweep"] = ("8338D5FE8D3E7EC2A9C9C3DFDD11EC1F", "9E9D673D8497AC8DF8FADD8A22F00FB7"),
        ["ssao"] = ("FA27CBCDBAA958D0DD88823BF393F7F1", "CF168CA665E5EF6CD6912489F25E6B56"),
        ["ssao blur"] = ("66C5807545145CBE37642479AE46A98E", "D41686230E0F2728B98F7C5E650E38DF"),
        ["composite"] = ("C8F526A5A427DE55F352250A02FCB4D3", "5491B42233C74B80EF0EABF6A0D9CADE"),
        ["fxaa"] = ("618C9A4EE1CF1E16E1DA169793332F5D", "F1FEC85D6019965BC0CF8C3FC6C3B7A6"),
        ["heat haze"] = ("BA035F48A7B3AA2B22EB12C6FA7DFE41", "7A9CF5D061D7D85DA120186DA23ABDFF"),
        ["luminance"] = ("9DC8512AB7DB8E3027F54BAA0498A1FC", "05245CEA5A0034AE5CE19798F6D1FBD3"),
        ["adapt"] = ("FCECA1F87085387D698279D67EE334F1", "A2512C08F24CC6A32AF688339984F01A"),
        ["velocity"] = ("56A1E50CF5B27B46526826CB05749E43", "876664328450DA06AC9CB3659641400F"),
        ["taa"] = ("D7BEF41CD1DD6EB96010FBAC960B1C19", "336808F4355F1C09106E598DC5926C15"),
        ["fog volumes"] = ("4897737C553E25BDDBA3C4008FF2EE01", "1ED3B9893A0EFD2DB7D33DD75949E27D"),
    };

    static string Hash(CompiledProgram p) =>
        Convert.ToHexString(SHA256.HashData([.. p.VertexSpirv, .. p.FragmentSpirv]))[..32];

    [Fact]
    [Slow]
    public void Every_world_program_compiles_to_the_SPIR_V_master_produced()
    {
        var plain = new GlslProgramCompiler(new ShaderCompileOptions { UseDiskCache = false, UseMemoryCache = false });
        var remap = new GlslProgramCompiler(new ShaderCompileOptions { UseDiskCache = false, UseMemoryCache = false, RemapClipDepth = true });
        var actual = new List<string>();
        var wrong = new List<string>();
        foreach (var (name, v, f) in GpuApiTests.WorldPrograms())
        {
            var h = (Hash(plain.Compile(v, f)), Hash(remap.Compile(v, f)));
            actual.Add($"[\"{name}\"] = (\"{h.Item1}\", \"{h.Item2}\"),");
            if (!Golden.TryGetValue(name, out var g) || g != h) wrong.Add(name);
        }
        Assert.True(wrong.Count == 0 && actual.Count == Golden.Count,
            $"legacy SPIR-V changed for: {string.Join(", ", wrong)}\nactual table:\n{string.Join("\n", actual)}");
    }
}
