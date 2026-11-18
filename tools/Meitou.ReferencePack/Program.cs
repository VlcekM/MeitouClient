using Meitou.ReferencePack;

return args switch
{
    [var output, .. var assemblies] when assemblies.Length > 0 => PublicizedSet.Write(output, assemblies),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        meitou-reference-pack <output folder> <assembly.dll>...
            writes a publicized reference copy of each assembly into the folder: every type and member public, method bodies
            replaced by a throw, manifest resources dropped, marked [ReferenceAssembly] so no runtime will load it
            (docs/modding.md "Reference pack"; tools/scripts/package-references.ps1 runs it)
        """);
    return 2;
}
