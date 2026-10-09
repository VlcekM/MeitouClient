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
        ["sky"] = ("E0D7BA02EC1A9A5A180C70E60D62C805", "38BAC79AFC5010325B3687D503DB030B"),
        ["water"] = ("5C6E40AFCCCE1F665DD6B895625CE4BC", "A2B45CBDE6941EBA8AC3F9C6B3F456E4"),
        ["water meitou"] = ("C2C5AC5DADEAB96B29B0B2165442C94D", "4D01215B10470F89874FBFA72B04B909"),
        ["debug overlay"] = ("2967439E6B38320ED5326B2EF7EA984C", "FFCCCC151047230FD630483A92FEA9E2"),
        ["terrain patch"] = ("7C0CA19933613C5052391B511CE33A21", "FA21EF2E29A33354EC6842F07CB0E382"),
        ["terrain mesh"] = ("1E76E466D09E0743D9021F17087911C1", "2E31AF1AEB3F5FA357BEBB36EB8176E4"),
        ["terrain patch depth"] = ("C4CD0528DB32E9B8B5C63C01DD63E9E2", "40EA8F7B5C66123A63F2E5DF2BB7E3A1"),
        ["terrain mesh depth"] = ("8B86D769A1F25EE902EB1B02FB188E92", "A05EBA696FB834240023646D8D10EE21"),
        ["foliage mesh"] = ("C17FE6A6176A39D0F39BBB32CDB3FD70", "F0E065D0C2AF0050136DCB48A949D4D6"),
        ["foliage grass"] = ("051F25851A64E933EB6710FE7BB754D2", "473C2FE48E4BA3D0C9473DC4371A09B9"),
        ["foliage grass motion"] = ("1669CBA644B8869EDE4844E9A0310B5A", "88D5753B3E90F2A3CF4EE314A2B816CF"),
        ["foliage depth"] = ("C9DB0731B3DF7A4ED5BA5686276F6D12", "096B3A079E5982455F14B70F52FE8225"),
        ["buildings"] = ("DEAF73FE8F38E4AFD08BDA058E837ECD", "AA89227DCCC5E2A83F915DD6DF44975F"),
        ["buildings depth"] = ("A54FFE418E48878E060098EDA796A99F", "066A9B73CA09D9E7E910F72969FFA788"),
        ["shadow debug"] = ("ECE841B17F1386CEF724543327E9412F", "2C069748BDE926274D59168E3A96EB3C"),
        ["shadow atlas"] = ("7A8BC32D82FC65DBE0B7F1BEE4AFB43A", "7C74B349400CB8F9819E044E7F6ED1BD"),
        ["shadow blocker"] = ("6D835FC54063D35442C133C38B55AB73", "C95F868A8A5DC350FE5803D310FF9F8A"),
        ["terrain shadow sweep"] = ("8338D5FE8D3E7EC2A9C9C3DFDD11EC1F", "9E9D673D8497AC8DF8FADD8A22F00FB7"),
        ["ssao"] = ("D3EA5B7E2B1A23E53E664B01F117723E", "3A6D21068AB10C7A9DB9F3B05040E372"),
        ["ssao blur"] = ("66C5807545145CBE37642479AE46A98E", "D41686230E0F2728B98F7C5E650E38DF"),
        ["composite"] = ("640B945E584A6D093CE2B4DADE82AA00", "153951C72A05222E85870B2556673FCD"),
        ["fxaa"] = ("618C9A4EE1CF1E16E1DA169793332F5D", "F1FEC85D6019965BC0CF8C3FC6C3B7A6"),
        ["heat haze"] = ("BA035F48A7B3AA2B22EB12C6FA7DFE41", "7A9CF5D061D7D85DA120186DA23ABDFF"),
        ["luminance"] = ("9DC8512AB7DB8E3027F54BAA0498A1FC", "05245CEA5A0034AE5CE19798F6D1FBD3"),
        ["adapt"] = ("FCECA1F87085387D698279D67EE334F1", "A2512C08F24CC6A32AF688339984F01A"),
        ["velocity"] = ("56A1E50CF5B27B46526826CB05749E43", "876664328450DA06AC9CB3659641400F"),
        ["taa"] = ("D7BEF41CD1DD6EB96010FBAC960B1C19", "336808F4355F1C09106E598DC5926C15"),
        ["fog volumes"] = ("947E8A3279BD32DA66DCABDE94890ABE", "3D3AD2712FE94CEA0BC858978DC55900"),
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
