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
        ["sky"] = ("73FA24C66D50155F75A06AE22AA7A45B", "FFCAF097BE7757C93C39579582DDEA33"),
        ["water"] = ("0E198F7E23AA3591D4DD20FD5AD36B22", "FE978EA4F4A4B563E4DD2A5CE8F5B430"),
        ["water meitou"] = ("C5DE66FBBC280CF742C7E567964812A4", "AE06767DFA35A3093F9E7F513F34D7E2"),
        ["debug overlay"] = ("2967439E6B38320ED5326B2EF7EA984C", "FFCCCC151047230FD630483A92FEA9E2"),
        ["terrain patch"] = ("073B3E504F8DCB25487AE4790BCC6A47", "DDC8DF2FCC19250FED1A2DBE6DA18544"),
        ["terrain mesh"] = ("465D67788AEDAE4EA0B56D2C3559723D", "90586E94B47943D2530FA8E76C259AE3"),
        ["terrain patch depth"] = ("C4CD0528DB32E9B8B5C63C01DD63E9E2", "40EA8F7B5C66123A63F2E5DF2BB7E3A1"),
        ["terrain mesh depth"] = ("8B86D769A1F25EE902EB1B02FB188E92", "A05EBA696FB834240023646D8D10EE21"),
        ["foliage mesh"] = ("A965F9BEC6C6EB8EB82A6F6EEAEEC2D4", "5FE336161E68983BAB46D4F99A7BE066"),
        ["foliage grass"] = ("4E01D396F75F1EE192752DD56EEC2CEB", "C7371FCA850A0A378B69470B9D1FF8B7"),
        ["foliage grass motion"] = ("1669CBA644B8869EDE4844E9A0310B5A", "88D5753B3E90F2A3CF4EE314A2B816CF"),
        ["foliage depth"] = ("C9DB0731B3DF7A4ED5BA5686276F6D12", "096B3A079E5982455F14B70F52FE8225"),
        ["buildings"] = ("887FCF04FDAC446EA555297F7FAD8DBD", "A6E76C73BB8A855DE33833439D091AB8"),
        ["buildings depth"] = ("A54FFE418E48878E060098EDA796A99F", "066A9B73CA09D9E7E910F72969FFA788"),
        ["shadow debug"] = ("ECE841B17F1386CEF724543327E9412F", "2C069748BDE926274D59168E3A96EB3C"),
        ["shadow atlas"] = ("7A8BC32D82FC65DBE0B7F1BEE4AFB43A", "7C74B349400CB8F9819E044E7F6ED1BD"),
        ["shadow blocker"] = ("6D835FC54063D35442C133C38B55AB73", "C95F868A8A5DC350FE5803D310FF9F8A"),
        ["terrain shadow sweep"] = ("8338D5FE8D3E7EC2A9C9C3DFDD11EC1F", "9E9D673D8497AC8DF8FADD8A22F00FB7"),
        ["ssao"] = ("8F9A15341FB09BE2426574444102F3D5", "C8129F34CCAF695965458D1C833083BF"),
        ["ssao blur"] = ("66C5807545145CBE37642479AE46A98E", "D41686230E0F2728B98F7C5E650E38DF"),
        ["composite"] = ("ABF6FA4BF04917E56CF24ABC8EDEB5F4", "22EF852559119F8942B370200DF3D951"),
        ["fxaa"] = ("618C9A4EE1CF1E16E1DA169793332F5D", "F1FEC85D6019965BC0CF8C3FC6C3B7A6"),
        ["heat haze"] = ("BA035F48A7B3AA2B22EB12C6FA7DFE41", "7A9CF5D061D7D85DA120186DA23ABDFF"),
        ["luminance"] = ("9DC8512AB7DB8E3027F54BAA0498A1FC", "05245CEA5A0034AE5CE19798F6D1FBD3"),
        ["adapt"] = ("7E619536EEC5C0954D8B5328F3E6065C", "180BD461DC1248416D6CFD4D2AB76761"),
        ["velocity"] = ("56A1E50CF5B27B46526826CB05749E43", "876664328450DA06AC9CB3659641400F"),
        ["taa"] = ("D7BEF41CD1DD6EB96010FBAC960B1C19", "336808F4355F1C09106E598DC5926C15"),
        ["fog volumes"] = ("2E20F380A038CCC5AFADE86BC49A2124", "2F60AD8A32F838D3B6035C4923A0047E"),
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
