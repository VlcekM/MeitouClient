using System.Reflection;

namespace ProvingGrounds.Core.Glue;

/// <summary>Task 9.2b: what src/Engine/PgJitWarmup.cs compiles ahead (pure reflection, no compiling here): every non-generic method and constructor
/// of the given assemblies' types, then the methods of the closed generic types of those assemblies that their fields, base types and interfaces
/// name (through arrays and generic arguments), each with its type's instantiation, and the nested types of each closed over the same arguments.
/// Abstract, generic, internal-call, runtime and P/Invoke methods are left out (as Meitou's own JitWarmup leaves them).</summary>
public static class JitWarmupList
{
    /// <summary>The methods to compile: (method, null) for a method of a non-generic type, (method, its type's arguments) for one of a closed
    /// generic type.</summary>
    public static List<(MethodBase Method, RuntimeTypeHandle[]? Instantiation)> Methods(IReadOnlyList<Assembly> assemblies)
    {
        var types = new List<Type>();
        foreach (var a in assemblies) types.AddRange(TypesOf(a));
        return Methods(types, new HashSet<Assembly>(assemblies));
    }

    /// <summary><see cref="Methods(IReadOnlyList{Assembly})"/> over the given types: theirs, and those of the closed generic types of
    /// <paramref name="ours"/> they name.</summary>
    public static List<(MethodBase Method, RuntimeTypeHandle[]? Instantiation)> Methods(IReadOnlyList<Type> types, ISet<Assembly> ours)
    {
        var list = new List<(MethodBase, RuntimeTypeHandle[]?)>();
        foreach (var type in types)
        {
            if (type.ContainsGenericParameters) continue;
            foreach (var m in MethodsOf(type)) list.Add((m, null));
        }
        foreach (var closed in ClosedGenerics(types, ours))
        {
            var args = closed.GetGenericArguments().Select(t => t.TypeHandle).ToArray();
            foreach (var m in MethodsOf(closed)) list.Add((m, args));
        }
        return list;
    }

    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static IEnumerable<Type> TypesOf(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
    }

    static IEnumerable<MethodBase> MethodsOf(Type type)
    {
        MethodBase[] methods;
        try { methods = [.. type.GetMethods(All), .. type.GetConstructors(All)]; }
        catch (Exception) { yield break; }
        foreach (var m in methods)
            if (!m.IsAbstract && !m.ContainsGenericParameters && m is not ConstructorInfo { IsStatic: true }   // a type initializer: never compiled ahead (its first run belongs to the type's first use)
                && (m.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0
                && (m.Attributes & MethodAttributes.PinvokeImpl) == 0)
                yield return m;
    }

    /// <summary>The closed generic types defined in <paramref name="ours"/> that the fields, base types and interfaces of <paramref name="types"/>
    /// name (looking through arrays and the arguments of any generic type), with the nested types of each closed over the same arguments.</summary>
    public static HashSet<Type> ClosedGenerics(IEnumerable<Type> types, ISet<Assembly> ours)
    {
        var found = new HashSet<Type>();
        void Visit(Type? t)
        {
            if (t is null) return;
            if (t.HasElementType) { Visit(t.GetElementType()); return; }
            if (!t.IsGenericType || t.ContainsGenericParameters)
            {
                if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) if (!a.IsGenericParameter) Visit(a);
                return;
            }
            var args = t.GetGenericArguments();
            foreach (var a in args) Visit(a);
            if (!ours.Contains(t.Assembly) || !found.Add(t)) return;
            foreach (var nested in t.GetGenericTypeDefinition().GetNestedTypes(All))
            {
                if (!nested.IsGenericTypeDefinition || nested.GetGenericArguments().Length != args.Length) continue;
                try { found.Add(nested.MakeGenericType(args)); } catch (Exception) { }
            }
        }
        foreach (var type in types)
        {
            try
            {
                Visit(type.BaseType);
                foreach (var i in type.GetInterfaces()) Visit(i);
                foreach (var f in type.GetFields(All)) Visit(f.FieldType);
            }
            catch (Exception) { }
        }
        return found;
    }
}
