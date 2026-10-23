using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace FendaoZhengfeng.Mod;

internal static class CharmPowerCmd
{
    internal static async Task<T?> Apply<T>(PlayerChoiceContext context, Creature target, decimal amount, Creature? applier, CardModel? cardSource, bool silent = false)
        where T : PowerModel
    {
        target = CharmTargeting.RoutePowerTarget(target, applier, CharmTargeting.PowerTypeFor<T>(amount));
        var applied = await PowerCmd.Apply<T>(context, target, amount, applier, cardSource, silent);
        // Native Apply can return a fresh zero-amount model after Artifact blocked
        // its installation. Never hand callers that unattached model as a live power.
        return applied != null && target.Powers.Contains(applied) ? applied : null;
    }

    internal static async Task<IReadOnlyList<T>> Apply<T>(PlayerChoiceContext context, IEnumerable<Creature>? targets, decimal amount, Creature? applier, CardModel? cardSource, bool silent = false)
        where T : PowerModel
    {
        var routed = (CharmTargeting.RoutePowerTargets(targets, applier, CharmTargeting.PowerTypeFor<T>(amount)) ?? []).ToArray();
        var result = new List<T>();
        foreach (var target in routed)
        {
            var applied = await Apply<T>(context, target, amount, applier, cardSource, silent);
            if (applied != null) result.Add(applied);
        }
        return result;
    }
}

internal static class CharmApplyCallsiteScanner
{
    private static readonly Lazy<MethodBase[]> callSites = new(ScanCallSites);

    private static readonly Dictionary<ushort, OpCode> opCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(opCode => unchecked((ushort)opCode.Value))
        .ToDictionary(group => group.Key, group => group.First());

    internal static IReadOnlyList<MethodBase> CallSites => callSites.Value;

    private static MethodBase[] ScanCallSites()
    {
        Stopwatch scanTimer = Stopwatch.StartNew();
        // Only scan the game and this Mod; unrelated libraries and third-party Mods are untouched.
        Assembly[] assemblies = [typeof(PowerCmd).Assembly, typeof(CharmPowerCmd).Assembly];
        var found = new HashSet<MethodBase>();
        foreach (Assembly assembly in assemblies.Distinct())
        {
            foreach (Type type in GetTypes(assembly))
            {
                // Verification harnesses contain async state machines with exception regions that
                // Harmony's dynamic re-emitter cannot safely transpile. They call CharmPowerCmd
                // explicitly, so leave every Native*Verification type and nested type untouched.
                if (assembly == typeof(CharmPowerCmd).Assembly && IsNativeVerificationType(type))
                    continue;

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                IEnumerable<MethodBase> methods;
                try
                {
                    methods = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
                }
                catch
                {
                    continue;
                }

                foreach (MethodBase method in methods)
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.DeclaringType?.ContainsGenericParameters == true)
                        continue;
                    if (ContainsGenericApplyCall(method))
                        found.Add(method);
                }
            }
        }

        MethodBase[] result = found.OrderBy(method => method.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();
        scanTimer.Stop();
        Godot.GD.Print($"[FDZF CHARM] Patched {result.Length} caller methods containing closed PowerCmd.Apply<T> calls (scan {scanTimer.Elapsed.TotalMilliseconds:F1} ms).");
        return result;
    }

    private static bool IsNativeVerificationType(Type type)
    {
        for (Type? declaringType = type; declaringType != null; declaringType = declaringType.DeclaringType)
        {
            string name = declaringType.Name;
            int genericSuffix = name.IndexOf('`');
            if (genericSuffix >= 0)
                name = name[..genericSuffix];
            if (name.StartsWith("Native", StringComparison.Ordinal)
                && name.EndsWith("Verification", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static IEnumerable<Type> GetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
        catch { return []; }
    }

    private static bool ContainsGenericApplyCall(MethodBase method)
    {
        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch { return false; }
        byte[]? il = body?.GetILAsByteArray();
        if (il == null) return false;

        int offset = 0;
        while (offset < il.Length)
        {
            ushort opcodeValue = il[offset++];
            if (opcodeValue == 0xFE)
            {
                if (offset >= il.Length) return false;
                opcodeValue = (ushort)(0xFE00 | il[offset++]);
            }
            if (!opCodesByValue.TryGetValue(opcodeValue, out OpCode opCode))
                return false;

            int operandStart = offset;
            int operandSize = OperandSize(opCode, il, operandStart);
            if (operandSize < 0 || offset + operandSize > il.Length) return false;

            if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
                && opCode.OperandType == OperandType.InlineMethod
                && operandSize == sizeof(int))
            {
                int token = BitConverter.ToInt32(il, operandStart);
                MethodBase? called;
                try { called = method.Module.ResolveMethod(token); }
                catch { called = null; }
                if (IsClosedApply(called)) return true;
            }
            offset += operandSize;
        }
        return false;
    }

    private static int OperandSize(OpCode opCode, byte[] il, int offset)
    {
        return opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod
                or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch when offset + sizeof(int) <= il.Length
                => sizeof(int) + BitConverter.ToInt32(il, offset) * sizeof(int),
            OperandType.InlineSwitch => -1,
            _ => -1
        };
    }

    private static bool IsClosedApply(MethodBase? candidate)
    {
        if (candidate is not MethodInfo method || method.DeclaringType != typeof(PowerCmd)
            || method.Name != nameof(PowerCmd.Apply) || !method.IsGenericMethod || method.ContainsGenericParameters)
            return false;
        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == 6
            && (parameters[1].ParameterType == typeof(Creature)
                || parameters[1].ParameterType == typeof(IEnumerable<Creature>));
    }

    internal static bool IsSingleApply(MethodInfo method)
        => method.GetParameters()[1].ParameterType == typeof(Creature);

    internal static MethodInfo GetWrapperDefinition(bool group)
        => typeof(CharmPowerCmd).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == nameof(CharmPowerCmd.Apply)
                && method.IsGenericMethodDefinition
                && (group
                    ? method.GetParameters()[1].ParameterType == typeof(IEnumerable<Creature>)
                    : method.GetParameters()[1].ParameterType == typeof(Creature)));
}

[HarmonyPatch]
internal static class CharmApplyCallsitePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => CharmApplyCallsiteScanner.CallSites;

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo called
                && called.DeclaringType == typeof(PowerCmd)
                && called.Name == nameof(PowerCmd.Apply)
                && called.IsGenericMethod
                && !called.ContainsGenericParameters
                && called.GetParameters().Length == 6)
            {
                bool group = !CharmApplyCallsiteScanner.IsSingleApply(called);
                instruction.operand = CharmApplyCallsiteScanner.GetWrapperDefinition(group)
                    .MakeGenericMethod(called.GetGenericArguments());
            }
            yield return instruction;
        }
    }
}
