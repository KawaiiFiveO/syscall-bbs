using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace LastCallBBS.ModAPI;

/// <summary>Thrown when game code doesn't have the expected shape, usually after a game update.</summary>
public sealed class ShapeMismatchException : Exception
{
    public ShapeMismatchException(string message) : base(message) { }
}

/// <summary>
/// Finds patch targets by the structure of the code. Most names in the game are scrambled and
/// can change between builds, so rely only on readable type names, .NET library members and
/// IL patterns. Every lookup demands exactly one match, because patching the wrong code is
/// worse than failing with a clear error.
/// </summary>
public static class CodeShape
{
    public const BindingFlags AllDeclared =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
        BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>Returns the only item, or throws naming what was expected and how many were found.</summary>
    public static T Single<T>(IEnumerable<T> items, string what)
    {
        List<T> found = items.ToList();
        if (found.Count != 1)
            throw new ShapeMismatchException($"expected exactly 1 {what}, found {found.Count}");
        return found[0];
    }

    /// <summary>Finds a type by its readable full name.</summary>
    public static Type RequireType(Assembly game, string fullName) =>
        game.GetType(fullName) ?? throw new ShapeMismatchException($"type {fullName} not found");

    /// <summary>Every method and constructor the type itself declares.</summary>
    public static IEnumerable<MethodBase> DeclaredMethods(Type type) =>
        type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared));

    /// <summary>The type's non-static constructors.</summary>
    public static IEnumerable<MethodBase> InstanceConstructors(Type type) =>
        type.GetConstructors(AllDeclared).Where(c => !c.IsStatic);

    /// <summary>The only field on the type satisfying the condition.</summary>
    public static FieldInfo SingleField(Type type, Func<FieldInfo, bool> condition, string what) =>
        Single(type.GetFields(AllDeclared).Where(condition), what);

    /// <summary>A method's original IL, or null if it has no body or can't be read.</summary>
    public static List<CodeInstruction> ReadIL(MethodBase method)
    {
        if (method.IsAbstract || method.GetMethodBody() == null)
            return null;
        try
        {
            return PatchProcessor.GetOriginalInstructions(method);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The only method among the candidates whose IL satisfies the condition.</summary>
    public static MethodBase SingleMethod(IEnumerable<MethodBase> candidates, Func<List<CodeInstruction>, bool> condition, string what) =>
        Single(candidates.Where(m =>
        {
            List<CodeInstruction> il = ReadIL(m);
            return il != null && condition(il);
        }), what);

    /// <summary>True for every IL branch or set instruction that orders two values.</summary>
    public static bool IsComparison(OpCode op) =>
        op == OpCodes.Blt || op == OpCodes.Blt_S || op == OpCodes.Blt_Un || op == OpCodes.Blt_Un_S ||
        op == OpCodes.Bge || op == OpCodes.Bge_S || op == OpCodes.Bge_Un || op == OpCodes.Bge_Un_S ||
        op == OpCodes.Bgt || op == OpCodes.Bgt_S || op == OpCodes.Bgt_Un || op == OpCodes.Bgt_Un_S ||
        op == OpCodes.Ble || op == OpCodes.Ble_S || op == OpCodes.Ble_Un || op == OpCodes.Ble_Un_S ||
        op == OpCodes.Clt || op == OpCodes.Clt_Un || op == OpCodes.Cgt || op == OpCodes.Cgt_Un;
}
