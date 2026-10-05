using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

using Meitou.Rendering;

namespace Meitou.ModelViewer;

/// <summary>How a character part is shaded (CharacterRenderer). Facts behind each mode: docs/characters.md.</summary>
public enum CharacterShading { Item, Body, Hair }

/// <summary>Textures and switches of one character part.</summary>
public sealed class CharacterMaterial
{
    public CharacterShading Shading { get; init; }
    public string Description { get; init; } = "";
    public string? Diffuse { get; init; }
    public string? Normal { get; init; }
    /// <summary>Clothing colour mask (R = colour 1, G = colour 2) with the dye colours and paint factors.</summary>
    public string? ColourMap { get; init; }
    public Vector4 Colour1 { get; init; }
    public Vector4 Colour2 { get; init; }
    public bool Dyed { get; init; }
    /// <summary>Worn items (Kenshi's Skinned material) cut where the normal map's alpha is below 0.6.</summary>
    public bool ClipOnNormalAlpha { get; init; }
    public bool DoubleSided { get; init; }

    // Hair meshes: brightness and alpha are channels of the diffuse texture.
    public Vector4 DiffuseChannel { get; init; } = new(1, 0, 0, 0);
    public Vector4 AlphaChannel { get; init; } = new(1, 0, 0, 0);
    public float AlphaThreshold { get; init; }
    public Vector3 HairColour { get; init; } = Vector3.One;

    // Body.
    public string? HeadDiffuse { get; init; }
    public string? HeadNormal { get; init; }
    public string? HeadMask { get; init; }
    public string? BodyMask { get; init; }
    public Vector3 SkinTone { get; init; }
    public string? HairOverlay { get; init; }
    public Vector4 HairOverlayAlpha { get; init; }
    public Vector4 HairOverlayMult { get; init; }
    public string? BeardOverlay { get; init; }
    public Vector4 BeardOverlayAlpha { get; init; }
    public List<BodyLayer> Vests { get; init; } = [];
    public Vector3 ShirtColour { get; init; }
    public bool HasShirtColour { get; init; }
}

/// <summary>A mesh of the character, positioned either by the body's skin matrices or by a bone.</summary>
public sealed class CharacterPartModel
{
    public required string Label { get; init; }
    public required string MeshPath { get; init; }
    public required Model Model { get; init; }
    public required CharacterMaterial Material { get; init; }
    public required AttachMode Mode { get; init; }
    /// <summary>For bone-attached parts: bone handle and offset (local rotation, then translation, in the bone's frame).</summary>
    public int Bone { get; init; } = -1;
    public Matrix4x4 Offset { get; init; } = Matrix4x4.Identity;
    /// <summary>The mesh's LOD levels (level 0 first; CharacterLod) and its bounding sphere for the LOD value.</summary>
    public IReadOnlyList<MeshLodLevel> Lods { get; init; } = [];
    public Vector3 BoundsCentre { get; init; }
    public float BoundsRadius { get; init; }
    /// <summary>The LOD level drawn (set by <see cref="CharacterScene.UpdateLod"/>).</summary>
    public int LodLevel { get; set; }

    public Matrix4x4 Transform(Animator animator) =>
        Mode == AttachMode.Bone && Bone >= 0 && Bone < animator.BoneCount ? Offset * animator.BoneTransform(Bone) : Matrix4x4.Identity;
}

/// <summary>Everything loaded for <c>--character</c>: the assembled appearance, part models, skeleton and animation layers.</summary>
public sealed class CharacterScene
{
    public required CharacterAppearance Appearance { get; init; }
    public required Animator Animator { get; init; }
    public required string SkeletonPath { get; init; }
    public List<CharacterPartModel> Parts { get; } = [];
    public List<AnimationLayer> Layers { get; } = [];
    public bool Paused { get; set; }
    public float Speed { get; set; } = 1;
    public int Selected { get; set; }
    public GameDatabase Database { get; init; } = null!;

    public void Advance(float dt)
    {
        if (Paused) return;
        foreach (var l in Layers) l.Advance(dt * Speed);
    }

    public void Pose() => Animator.Pose(Layers);

    /// <summary>Forced LOD level for every part (clamped to each mesh's levels), or null for Kenshi's distance rule.</summary>
    public int? ForcedLod { get; set; }
    /// <summary>Set when <see cref="UpdateLod"/> changed a level (cleared by the caller).</summary>
    public bool LodChanged { get; set; }

    /// <summary>
    /// Picks each part's LOD level: forced, or by the distance_sphere value from <paramref name="eye"/> (each entity on its
    /// own, as in Kenshi: docs/formats/ogre-mesh.md, "LOD"). Returns true when a level changed.
    /// </summary>
    public bool UpdateLod(Vector3 eye)
    {
        bool changed = false;
        foreach (var part in Parts)
        {
            if (part.Lods.Count == 0) continue;
            int level = ForcedLod is { } f ? Math.Clamp(f, 0, part.Lods.Count - 1)
                : CharacterLod.Select(part.Lods, CharacterLod.Value(eye, Vector3.Transform(part.BoundsCentre, part.Transform(Animator)), part.BoundsRadius));
            if (part.Lods[level].ManualMesh is not null) level = 0; // manual levels (separate meshes) are not loaded
            changed |= level != part.LodLevel;
            part.LodLevel = level;
        }
        LodChanged |= changed;
        return changed;
    }

    /// <summary>A layer for an ANIMATION record name or an Ogre animation name, with that record's deletions and override bones.</summary>
    public AnimationLayer? Layer(string name, float weight)
    {
        var mask = AnimationMask.Find(Database, name);
        var animation = Animator.Animations.FirstOrDefault(a => string.Equals(a.Name, mask.AnimationName, StringComparison.OrdinalIgnoreCase))
            ?? Animator.Animations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (animation is null) return null;
        return new AnimationLayer
        {
            Animation = animation, Weight = weight,
            Excluded = Animator.Handles(mask.DeletedBones), Override = Animator.Handles(mask.OverrideBones),
            Label = mask.RecordName is { } r && r != animation.Name ? $"{r} ({animation.Name})" : animation.Name,
        };
    }

    public static CharacterScene? Load(GameInstall install, GameDatabase db, AssetLocator assets, MaterialResolver resolver, CharacterViewOptions o)
    {
        CharacterAppearance c;
        try
        {
            c = CharacterAppearance.Build(db, install.Root, o.Character!, new CharacterOptions
            {
                Female = o.Female, Equip = o.Equip, Naked = o.Naked, WeaponDrawn = o.Drawn, Seed = o.Seed, Faction = o.Faction,
            });
        }
        catch (KeyNotFoundException e)
        {
            Console.Error.WriteLine(e.Message);
            return null;
        }
        string who = c.Character is { } ch ? $"CHARACTER '{ch.Name}' ({ch.StringId})" : $"RACE '{c.Race.Name}'";
        Console.WriteLine($"character {who}, race '{c.Race.Name}', {(c.Female ? "female" : "male")}" + (c.BodyFile is { } bf ? $", body {bf}" : ", no body file"));
        if (c.Loadout is { } loadout) foreach (var line in loadout.Describe().Where(l => !l.StartsWith("  note:", StringComparison.Ordinal))) Console.WriteLine($"loadout   {line.Trim()}");
        foreach (var n in c.Notes) Console.WriteLine($"note      {n}");

        // Body mesh and its skeleton (the body's own link; Kenshi shares that instance with worn items).
        var bodyPath = FindMesh(assets, c.BodyMesh);
        if (bodyPath is null) { Console.Error.WriteLine($"Body mesh not found: {c.BodyMesh}"); return null; }
        var bodyMesh = OgreMeshReader.ReadFile(bodyPath);
        var skeletonPath = bodyMesh.SkeletonName is { } sk ? assets.Find(sk) : null;
        if (skeletonPath is null) { Console.Error.WriteLine($"[AppearanceBase::buildBody] {c.BodyMesh} has no skeleton."); return null; }
        var animator = new Animator(OgreSkeletonReader.ReadFile(skeletonPath));
        Console.WriteLine($"body      {assets.Relative(bodyPath)}, skeleton {assets.Relative(skeletonPath)} ({animator.BoneCount} bones, {animator.Animations.Count} animations)");

        var scene = new CharacterScene { Appearance = c, Animator = animator, SkeletonPath = skeletonPath, Database = db, ForcedLod = o.Lod };
        ApplyShape(db, c, animator, o);
        var bodyModel = Model.Build(bodyMesh, animator.BoneCount);
        // Slaves (CHARACTER "shaved") get cut horns on a mesh with bone_horns_top_short (CharacterShape.BakeWeight).
        bool cutHorns = c.Character?.GetBool("shaved") == true && bodyMesh.Poses.Any(p => p.Name == "bone_horns_top_short");
        if (!o.NoMorphs) ApplyPoses(bodyMesh, bodyModel, c.PoseWeights, cutHorns);
        scene.Parts.Add(new CharacterPartModel
        {
            Label = $"body {Path.GetFileName(bodyPath)}", MeshPath = bodyPath, Model = bodyModel, Mode = AttachMode.SharedSkeleton,
            Material = BodyMaterial(db, c, o),
            Lods = CharacterLod.Levels(bodyMesh), BoundsCentre = BoundsCentre(bodyMesh, bodyModel), BoundsRadius = BoundsRadius(bodyMesh, bodyModel),
        });
        Console.WriteLine($"head      {(c.Head is { } h ? $"HEAD '{h.Name}'" : "none")}; hair {(c.Hair?.Name ?? "none")}; beard {(c.Beard?.Name ?? "none")}");
        if (c.BodyLayers.Count > 0) Console.WriteLine("layers    " + string.Join(", ", c.BodyLayers.Select(l => $"'{l.Record.Name}' vest {Path.GetFileName(l.Diffuse)}")));

        foreach (var part in c.Parts)
        {
            var path = FindMesh(assets, part.Mesh);
            if (path is null) { Console.WriteLine($"part      {part.Record.Type} '{part.Record.Name}' .{part.Field}: mesh {part.Mesh} not found"); continue; }
            OgreMesh mesh;
            try { mesh = OgreMeshReader.ReadFile(path); }
            catch (Exception e) when (e is OgreFormatException or IOException) { Console.WriteLine($"part      {path}: {e.Message}"); continue; }
            bool shared = part.Mode == AttachMode.SharedSkeleton;
            var model = Model.Build(mesh, shared ? animator.BoneCount : null);
            foreach (var w in model.Warnings) Console.WriteLine($"warning   {Path.GetFileName(path)}: {w}");
            int bone = -1;
            var offset = Matrix4x4.Identity;
            string where = "shares the body skeleton";
            if (!shared)
            {
                // Mode 0: attachObjectToBone at the attachment point; a bone the skeleton lacks falls back to the root.
                var point = part.Point is { } pn && c.AttachmentPoints.TryGetValue(pn, out var ap) ? ap : null;
                bone = point is null ? -1 : animator.Handle(point.Bone) ?? -1;
                if (bone < 0) bone = animator.Skeleton.Bones.FirstOrDefault(b => b.Parent is null)?.Handle ?? 0;
                if (point is not null) offset = Matrix4x4.CreateFromQuaternion(point.Rotation) * Matrix4x4.CreateTranslation(point.Position);
                where = $"at '{part.Point}' on {animator.Skeleton.FindBone((ushort)bone)?.Name}" + (point is null ? " (no such point: root bone)" : "");
            }
            var material = PartMaterial(db, resolver, c, part, path, mesh);
            scene.Parts.Add(new CharacterPartModel
            {
                Label = $"{part.Record.Type} '{part.Record.Name}' .{part.Field}", MeshPath = path, Model = model, Mode = part.Mode,
                Bone = bone, Offset = offset, Material = material,
                Lods = CharacterLod.Levels(mesh), BoundsCentre = BoundsCentre(mesh, model), BoundsRadius = BoundsRadius(mesh, model),
            });
            Console.WriteLine($"part      {part.Record.Type} '{part.Record.Name}' .{part.Field} -> {assets.Relative(path)} {where}; {material.Description}");
        }

        // Animations: --anim list, else the body file's idle stance, else the binding pose.
        var anims = o.Animations.Count > 0 ? o.Animations : c.Body?.IdleStance is { } idle && !o.BindPose ? [(idle, 1f)] : [];
        foreach (var (name, weight) in anims)
        {
            if (scene.Layer(name, weight) is { } layer)
            {
                layer.Time = o.Time;
                scene.Layers.Add(layer);
                Console.WriteLine($"anim      {layer.Label} weight {weight:0.##}, {layer.Animation.Length:0.##} s" +
                    (layer.Excluded.Count > 0 ? $", {layer.Excluded.Count} tracks deleted" : "") + (layer.Override.Count > 0 ? $", {layer.Override.Count} override bones" : ""));
            }
            else Console.WriteLine($"anim      '{name}' not found");
        }
        // Posture sliders (animation.md): one-frame pose libraries held at length × slider / 100, weight 1.
        if (c.Body is { } bodyFile && !o.NoPostures)
            foreach (var (animation, slider) in new[] { ("postures", "Posture"), ("neck set", "Neck position"), ("shoulder set", "Shoulder set") })
                if (bodyFile.Floats.ContainsKey(slider) && scene.Layer(animation, 1) is { } posture)
                {
                    posture.Time = posture.Animation.Length * bodyFile.Get(slider) / 100;
                    posture.Speed = 0;
                    scene.Layers.Add(posture);
                    Console.WriteLine($"posture   {animation} held at {bodyFile.Get(slider):0.#}% ({slider})");
                }
        scene.Pose();
        return scene;
    }

    /// <summary>Meshes are loaded by bare name (Kenshi reduces the path first), so resources.cfg's last location wins.</summary>
    static string? FindMesh(AssetLocator assets, string fcsPath)
    {
        var bare = Path.GetFileName(fcsPath.Replace('\\', '/'));
        return assets.Configured.Find(bare) ?? assets.Find(fcsPath);
    }

    /// <summary>Kenshi bakes every non-zero pose into a clone of the body mesh (positions and normals; ogre-mesh.md).</summary>
    static void ApplyPoses(OgreMesh mesh, Model model, IReadOnlyDictionary<string, float> weights, bool cutHorns)
    {
        int applied = 0;
        foreach (var pose in mesh.Poses)
        {
            // The stored value is the weight, unscaled (docs/formats/ogre-mesh.md, "Poses").
            if (!weights.TryGetValue(pose.Name, out float stored) || CharacterShape.BakeWeight(pose.Name, stored, cutHorns) is not { } w) continue;
            var part = model.Parts.FirstOrDefault(p => p.SubMeshIndex == pose.Target - 1);
            if (part is null) continue;
            foreach (var v in pose.Vertices)
            {
                if (v.Index >= part.Vertices.Length) continue;
                part.Vertices[v.Index].Position += v.Offset * w;
                if (pose.IncludesNormals) part.Vertices[v.Index].Normal = Vector3.Normalize(part.Vertices[v.Index].Normal + v.Normal * w);
            }
            applied++;
        }
        if (applied > 0) Console.WriteLine($"morphs    {applied} of {mesh.Poses.Count} poses baked in" + (cutHorns ? " (cut horns)" : ""));
    }

    /// <summary>Body-shape sliders as bone sizes (CharacterShape; docs/animation.md, "Body shape sliders"), with --shape overrides.</summary>
    static void ApplyShape(GameDatabase db, CharacterAppearance c, Animator animator, CharacterViewOptions o)
    {
        if (o.NoShape) return;
        if (animator.BoneCount != CharacterShape.HumanBoneCount)
        {
            if (o.Shape.Count > 0) Console.WriteLine($"shape     skeleton has {animator.BoneCount} bones, not {CharacterShape.HumanBoneCount}: no body shape (as in Kenshi)");
            return;
        }
        var input = CharacterShape.InputFor(db, c);
        var defaulted = CharacterShape.Sliders.Where(s => !input.Sliders.ContainsKey(s) && (c.Female || !s.StartsWith("Breast ", StringComparison.Ordinal))).ToList();
        input = CharacterShape.Override(input, o.Shape, out var applied);
        var shape = CharacterShape.Compute(input, name => animator.Handle(name) is not null);
        animator.SetShape(shape.BoneSize, shape.PositionalSize);
        animator.MovementScale = shape.MovementScale;
        Console.WriteLine($"shape     {(c.Body is null ? "no body file" : "body file sliders")}, muscle {input.Muscle:0.##}, starvation {input.Starvation:0.##}, movement scale {shape.MovementScale:0.###}" +
            (defaulted.Count > 0 && c.Body is not null ? $"; missing sliders at 100: {string.Join(", ", defaulted)}" : "") +
            (applied.Count > 0 ? $"; --shape {string.Join(", ", applied)}" : ""));
    }

    // LOD value inputs: the mesh file's bounds (centre of the box, sphere radius), else the model's.
    static Vector3 BoundsCentre(OgreMesh mesh, Model model) => mesh.Bounds is { } b ? (b.Min + b.Max) / 2 : model.Center;
    static float BoundsRadius(OgreMesh mesh, Model model) => mesh.Bounds is { } b ? b.Radius : model.Radius;

    static Vector4 Channel(int channel) => channel switch { 1 => new(0, 1, 0, 0), 2 => new(0, 0, 1, 0), 3 => new(0, 0, 0, 1), _ => new(1, 0, 0, 0) };

    static string? Tex(GameRecord? r, string field) => r?.GetPath(field) is { Length: > 0 } p ? p : null;

    static CharacterMaterial BodyMaterial(GameDatabase db, CharacterAppearance c, CharacterViewOptions o)
    {
        string f = c.Female ? " female" : "";
        // Shirt colour for vest colour masks: the CHARACTER's colour record (dyes of the faction are not followed).
        var colour = c.Character?.GetReferences("color").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(x => x is not null);
        return new CharacterMaterial
        {
            Shading = CharacterShading.Body,
            Description = $"RACE '{c.Race.Name}' body",
            Diffuse = c.BodyTexture, Normal = c.BodyNormal, BodyMask = c.BodyMask,
            HeadDiffuse = Tex(c.Head, "texture map"), HeadNormal = Tex(c.Head, "normal map"), HeadMask = Tex(c.Head, "mask map"),
            SkinTone = o.NoSkinTone ? Vector3.Zero : c.SkinToneParameter,
            HairOverlay = Tex(c.Hair, "head texture" + f),
            HairOverlayAlpha = Channel(c.Hair?.GetInt("head alpha channel" + f, 1) ?? 1),
            HairOverlayMult = Channel(c.Hair?.GetInt("head channel" + f) ?? 0),
            BeardOverlay = Tex(c.Beard, "head texture" + f),
            BeardOverlayAlpha = Channel(c.Beard?.GetInt("head alpha channel" + f, 1) ?? 1),
            HairColour = c.HairColour,
            Vests = c.BodyLayers.Take(3).ToList(),
            ShirtColour = colour is null ? Vector3.One : CharacterAppearance.Rgb(colour.GetInt("color 1")),
            HasShirtColour = colour is not null,
        };
    }

    static CharacterMaterial PartMaterial(GameDatabase db, MaterialResolver resolver, CharacterAppearance c, CharacterPart part, string path, OgreMesh mesh)
    {
        var item = part.Record;
        string f = c.Female ? " female" : "";
        if (item.Type == FcsRecordType.ATTACHMENT)
            return new CharacterMaterial
            {
                Shading = CharacterShading.Hair, Description = $"hair texture {Path.GetFileName(Tex(item, "texture map" + f) ?? "-")}",
                Diffuse = Tex(item, "texture map" + f) ?? Tex(item, "texture map"),
                DiffuseChannel = Channel(item.GetInt("hair diffuse channel")), AlphaChannel = Channel(item.GetInt("hair alpha channel")),
                AlphaThreshold = item.GetInt("alpha rejection", 128) / 255f, HairColour = c.HairColour, DoubleSided = true,
            };

        bool worn = part.Mode == AttachMode.SharedSkeleton;
        // Clothing: MATERIAL_SPECS_CLOTHING from "material" (female: "material female" if set), likeliest first; dye from
        // the item's "color" COLOR_DATA (else the CHARACTER's, unless "dont colorise").
        var refs = item.GetReferences(c.Female && item.GetReferences("material female").Count > 0 ? "material female" : "material");
        var spec = refs.OrderByDescending(r => r.Values.Value0).Select(r => db.Find(r.TargetStringId))
            .FirstOrDefault(s => s is { Type: FcsRecordType.MATERIAL_SPECS_CLOTHING or FcsRecordType.MATERIAL_SPEC or FcsRecordType.MATERIAL_SPECS_WEAPON });
        // A generated loadout chose the clothing material (from "material"; "material female" still wins for women).
        if (part.Material is { Type: FcsRecordType.MATERIAL_SPECS_CLOTHING } chosen && !(c.Female && item.GetReferences("material female").Count > 0))
            spec = chosen;
        if (spec is not null)
        {
            var dye = item.GetReferences("color").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(x => x is not null);
            if (dye is null && !item.GetBool("dont colorise", true))
                dye = c.Character?.GetReferences("color").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(x => x is not null);
            bool dyed = dye is not null && Tex(spec, "color map") is not null;
            return new CharacterMaterial
            {
                Shading = CharacterShading.Item, Description = $"{spec.Type} '{spec.Name}'" + (dyed ? $", dye '{dye!.Name}'" : ""),
                Diffuse = Tex(spec, "texture map"), Normal = Tex(spec, "normal map"),
                ColourMap = dyed ? Tex(spec, "color map") : null, Dyed = dyed,
                Colour1 = dyed ? new Vector4(CharacterAppearance.Rgb(dye!.GetInt("color 1")), spec.GetFloat("paint factor 1")) : default,
                Colour2 = dyed ? new Vector4(CharacterAppearance.Rgb(dye!.GetInt("color 2")), spec.GetFloat("paint factor 2")) : default,
                ClipOnNormalAlpha = worn && Tex(spec, "normal map") is not null,
                DoubleSided = spec.GetInt("material type") == 2,
            };
        }
        // A weapon of a generated loadout: its manufacturer's model.
        if (part.Material is { Type: FcsRecordType.MATERIAL_SPECS_WEAPON } model && Tex(model, "texture map") is { } modelTexture)
            return new CharacterMaterial
            {
                Shading = CharacterShading.Item, Description = $"MATERIAL_SPECS_WEAPON '{model.Name}' (loadout model)",
                Diffuse = modelTexture, Normal = Tex(model, "normal map"), ClipOnNormalAlpha = false,
            };
        // Weapons and anything else: the viewer's resolver (weapon models of the manufacturers, own textures, script material).
        var candidate = resolver.Candidates(path, mesh.SubMeshes[0]).FirstOrDefault();
        return new CharacterMaterial
        {
            Shading = CharacterShading.Item, Description = candidate?.Description ?? "untextured",
            Diffuse = candidate?.Diffuse, Normal = candidate?.Normal, ClipOnNormalAlpha = false, DoubleSided = candidate?.DoubleSided ?? false,
        };
    }
}
