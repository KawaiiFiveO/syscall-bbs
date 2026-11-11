using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace LastCallBBS.Launcher;

/// <summary>The scope names a mod can declare in mod.ini.</summary>
internal static class ModScopes
{
    public const string Full = "full";

    public const string Content = "content";

    public const string Programs = "programs";

    public static readonly ISet<string> Known = new HashSet<string> { Full, Content, Programs, "game", "terminal", "desktop", "story", "foodcourt" };

    /// <summary>The scope a hook belongs to: "FoodCourt.TickCompleted" is in "foodcourt".</summary>
    public static string OfHook(string hook) => hook.Substring(0, hook.IndexOf('.')).ToLowerInvariant();
}

/// <summary>
/// Checks, before a scoped mod is loaded, that its code can't reach past the mod API. This stops
/// accidents and makes a mod's reach visible. It isn't a security boundary: mods run with full
/// trust, and determined code can get around it.
/// </summary>
internal static class ModScanner
{
    private static readonly string[] BlockedAssemblies = { "0Harmony", "Mono.Cecil", "Steamworks.NET" };

    /// <summary>Returns a description of each problem in the folder's DLLs.</summary>
    public static List<string> Scan(string folder, string gameAssembly)
    {
        var problems = new List<string>();
        foreach (string dll in Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            string file = Path.GetFileName(dll);
            try
            {
                using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters(ReadingMode.Deferred));
                foreach (ModuleDefinition module in assembly.Modules)
                    problems.AddRange(ScanModule(module, gameAssembly).Distinct().Select(p => $"{file}: {p}"));
            }
            catch (BadImageFormatException)
            {
                problems.Add($"{file}: not a .NET assembly, so it can't be checked");
            }
        }
        return problems;
    }

    private static IEnumerable<string> ScanModule(ModuleDefinition module, string gameAssembly)
    {
        foreach (AssemblyNameReference reference in module.AssemblyReferences)
        {
            if (reference.Name == gameAssembly)
                yield return "references the game directly";
            else if (BlockedAssemblies.Contains(reference.Name) || reference.Name.StartsWith("MonoMod"))
                yield return $"references {reference.Name}";
        }

        foreach (TypeReference type in module.GetTypeReferences())
        {
            // MemberInfo appears for anything as ordinary as GetType().Name, and can't invoke anything itself.
            if ((type.Namespace == "System.Reflection" || type.Namespace.StartsWith("System.Reflection.")) &&
                !type.Name.EndsWith("Attribute") && type.Name != "MemberInfo")
                yield return $"uses reflection ({type.Name})";
        }

        foreach (MemberReference member in module.GetMemberReferences())
        {
            if (IsDynamicLookup(member))
                yield return $"uses {member.DeclaringType.Name}.{member.Name}";
        }

        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (method.IsPInvokeImpl)
                    yield return $"calls native code ({method.Name})";
            }
        }
    }

    // Most reflection is caught through its return types. These calls don't expose any.
    private static bool IsDynamicLookup(MemberReference member)
    {
        string owner = member.DeclaringType?.FullName;
        if (owner == "System.Type")
            return member.Name == "GetType" || member.Name == "InvokeMember";
        if (owner != "System.Activator")
            return false;
        // "new T()" in generic code compiles to Activator.CreateInstance<T>(), which is harmless.
        return !(member is MethodReference method && method.Name == "CreateInstance" && method.Parameters.Count == 0 && IsGeneric(method));
    }

    private static bool IsGeneric(MethodReference method) => method.HasGenericParameters || method is GenericInstanceMethod;
}
