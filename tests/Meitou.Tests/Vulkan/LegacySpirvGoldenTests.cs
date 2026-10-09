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
        ["sky"] = ("62555FC5F60E69054BA147D55C20A54A", "1643FF0621CBECD18A529BEA270FA741"),
        ["water"] = ("36732A054E129052A4C949B5996A5B1B", "E3A723F15DBB8FE1D4ADF1947FCA4787"),
        ["water meitou"] = ("908BB1A5A80C9D9DB0A9EE8323A6911E", "AB89C01F6C0B2E43C9A77A9469F5B9E8"),
        ["debug overlay"] = ("2967439E6B38320ED5326B2EF7EA984C", "FFCCCC151047230FD630483A92FEA9E2"),
        ["terrain patch"] = ("F0EAEB047438659D804F7161858AF04D", "539D1718F8DC302C0CB802476AF5D261"),
        ["terrain mesh"] = ("761E0DDE05A7DD19AAF8ED5B3214457A", "4878161D504B44D3A3D3C4F83A298BF9"),
        ["terrain patch depth"] = ("C4CD0528DB32E9B8B5C63C01DD63E9E2", "40EA8F7B5C66123A63F2E5DF2BB7E3A1"),
        ["terrain mesh depth"] = ("8B86D769A1F25EE902EB1B02FB188E92", "A05EBA696FB834240023646D8D10EE21"),
        ["foliage mesh"] = ("DE18A0FB6E95789B3D82EC6A8DC9F9D7", "783475ABF367392ACE1BB964424BDA14"),
        ["foliage grass"] = ("3234FD86C41607594B638A22186F7441", "838C1F5C154B8AC9F3758ABB0760A4B3"),
        ["foliage grass motion"] = ("1669CBA644B8869EDE4844E9A0310B5A", "88D5753B3E90F2A3CF4EE314A2B816CF"),
        ["foliage depth"] = ("C9DB0731B3DF7A4ED5BA5686276F6D12", "096B3A079E5982455F14B70F52FE8225"),
        ["buildings"] = ("D6DD38D60429840960F0AE29AC997E14", "84E5628722215C37981FD29F676DB3FB"),
        ["buildings depth"] = ("A54FFE418E48878E060098EDA796A99F", "066A9B73CA09D9E7E910F72969FFA788"),
        ["shadow debug"] = ("ECE841B17F1386CEF724543327E9412F", "2C069748BDE926274D59168E3A96EB3C"),
        ["shadow atlas"] = ("7A8BC32D82FC65DBE0B7F1BEE4AFB43A", "7C74B349400CB8F9819E044E7F6ED1BD"),
        ["shadow blocker"] = ("6D835FC54063D35442C133C38B55AB73", "C95F868A8A5DC350FE5803D310FF9F8A"),
        ["terrain shadow sweep"] = ("8338D5FE8D3E7EC2A9C9C3DFDD11EC1F", "9E9D673D8497AC8DF8FADD8A22F00FB7"),
        ["ssao"] = ("4B1B854A3ECE3604B2C8C44D727E98F7", "3BBDF3CF9A0ED354ACC40500797C0FD4"),
        ["ssao blur"] = ("66C5807545145CBE37642479AE46A98E", "D41686230E0F2728B98F7C5E650E38DF"),
        ["composite"] = ("640B945E584A6D093CE2B4DADE82AA00", "153951C72A05222E85870B2556673FCD"),
        ["fxaa"] = ("618C9A4EE1CF1E16E1DA169793332F5D", "F1FEC85D6019965BC0CF8C3FC6C3B7A6"),
        ["heat haze"] = ("BA035F48A7B3AA2B22EB12C6FA7DFE41", "7A9CF5D061D7D85DA120186DA23ABDFF"),
        ["luminance"] = ("9DC8512AB7DB8E3027F54BAA0498A1FC", "05245CEA5A0034AE5CE19798F6D1FBD3"),
        ["adapt"] = ("FCECA1F87085387D698279D67EE334F1", "A2512C08F24CC6A32AF688339984F01A"),
        ["velocity"] = ("56A1E50CF5B27B46526826CB05749E43", "876664328450DA06AC9CB3659641400F"),
        ["taa"] = ("D7BEF41CD1DD6EB96010FBAC960B1C19", "336808F4355F1C09106E598DC5926C15"),
        ["fog volumes"] = ("D4A366A2F79DCA734658D8FFFB4C7455", "C619865514A3752D9B621FE3DE4EED2A"),
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
