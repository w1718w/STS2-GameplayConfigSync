using Mono.Cecil;
using Mono.Cecil.Cil;

const string HarmonyPatchAttribute = "HarmonyLib.HarmonyPatch";
string[] patchMethodNames = ["Prefix", "Postfix", "Transpiler", "Finalizer"];

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: GameplayConfigSync.ContractChecks <mod-assembly> <game-assembly>");
    return 2;
}

string modAssemblyPath = Path.GetFullPath(args[0]);
string gameAssemblyPath = Path.GetFullPath(args[1]);

if (!File.Exists(modAssemblyPath) || !File.Exists(gameAssemblyPath))
{
    Console.Error.WriteLine(
        $"[GCSH000] Assembly not found. mod={modAssemblyPath} game={gameAssemblyPath}");
    return 2;
}

using ModuleDefinition mod = ModuleDefinition.ReadModule(modAssemblyPath);
using ModuleDefinition game = ModuleDefinition.ReadModule(gameAssemblyPath);
bool gameIsReferenceAssembly = game.Assembly.CustomAttributes.Any(attribute =>
    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute");

Dictionary<string, TypeDefinition> gameTypes = GetAllTypes(game.Types)
    .ToDictionary(type => type.FullName, StringComparer.Ordinal);
HashSet<MethodDefinition> visited = [];
List<string> errors = [];
List<string> warnings = [];
int patchTypeCount = 0;
int patchMethodCount = 0;

foreach (TypeDefinition patchType in GetAllTypes(mod.Types))
{
    PatchTarget? target = ReadPatchTarget(patchType);
    if (target is null)
        continue;

    patchTypeCount++;
    if (!gameTypes.TryGetValue(target.Type.FullName, out TypeDefinition? targetType))
    {
        AddMissingTargetDiagnostic(
            $"{patchType.FullName}: target type {target.Type.FullName} does not exist.");
        continue;
    }

    List<MethodDefinition> targetMethods = targetType.Methods
        .Where(method => method.Name == target.MethodName)
        .ToList();
    if (targetMethods.Count == 0)
    {
        AddMissingTargetDiagnostic(
            $"{patchType.FullName}: target method {target.Type.FullName}.{target.MethodName} does not exist.");
        continue;
    }

    bool[] staticKinds = targetMethods.Select(method => method.IsStatic).Distinct().ToArray();
    if (staticKinds.Length != 1)
    {
        errors.Add(
            $"[GCSH002] {patchType.FullName}: target {target.Type.FullName}.{target.MethodName} " +
            "has both static and instance overloads; declare argument types in HarmonyPatch.");
        continue;
    }

    foreach (MethodDefinition patchMethod in patchType.Methods.Where(IsPatchMethod))
    {
        patchMethodCount++;
        ParameterDefinition? instanceParameter = patchMethod.Parameters
            .SingleOrDefault(parameter => parameter.Name == "__instance");
        if (instanceParameter is not null &&
            instanceParameter.ParameterType.FullName != target.Type.FullName)
        {
            errors.Add(
                $"[GCSH003] {patchMethod.FullName}: __instance is {instanceParameter.ParameterType.FullName}, " +
                $"but the patch target is {target.Type.FullName}.");
        }

        if (staticKinds[0])
            continue;

        visited.Clear();
        foreach (MethodDefinition reachable in GetReachableLocalMethods(patchMethod, mod, visited))
        {
            foreach (Instruction instruction in reachable.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference called ||
                    called.DeclaringType.FullName != target.Type.FullName ||
                    called.Name != "get_Instance" ||
                    called.HasThis)
                {
                    continue;
                }

                errors.Add(
                    $"[GCSH001] {patchMethod.FullName} patches instance method " +
                    $"{target.Type.FullName}.{target.MethodName}, but reachable method {reachable.FullName} " +
                    $"calls {target.Type.FullName}.Instance. Pass Harmony's __instance instead; " +
                    "the singleton may not be assigned while the target object is being constructed.");
            }
        }
    }
}

if (patchTypeCount == 0 || patchMethodCount == 0)
{
    errors.Add("[GCSH004] No Harmony patch contracts were discovered; attribute parsing may be broken.");
}

string[] distinctErrors = errors.Distinct(StringComparer.Ordinal).ToArray();
foreach (string warning in warnings.Distinct(StringComparer.Ordinal))
    Console.WriteLine(warning);
foreach (string error in distinctErrors)
    Console.Error.WriteLine(error);

if (distinctErrors.Length > 0)
{
    Console.Error.WriteLine($"Harmony contract checks failed with {distinctErrors.Length} error(s).");
    return 1;
}

Console.WriteLine(
    $"Harmony contract checks passed: {patchTypeCount} target(s), {patchMethodCount} patch method(s).");
return 0;

void AddMissingTargetDiagnostic(string message)
{
    if (gameIsReferenceAssembly)
    {
        warnings.Add(
            $"[GCSH102] {message} The metadata-only reference assembly may omit private members; " +
            "the strict check will run when a real game assembly is available.");
    }
    else
    {
        errors.Add($"[GCSH002] {message}");
    }
}

PatchTarget? ReadPatchTarget(TypeDefinition patchType)
{
    TypeReference? targetType = null;
    string? targetMethod = null;

    foreach (CustomAttribute attribute in patchType.CustomAttributes
                 .Where(attribute => attribute.AttributeType.FullName == HarmonyPatchAttribute))
    {
        foreach (CustomAttributeArgument argument in attribute.ConstructorArguments)
        {
            if (argument.Value is TypeReference type)
                targetType ??= type;
            else if (argument.Value is string methodName)
                targetMethod ??= methodName;
        }
    }

    return targetType is not null && targetMethod is not null
        ? new PatchTarget(targetType, targetMethod)
        : null;
}

bool IsPatchMethod(MethodDefinition method) =>
    patchMethodNames.Contains(method.Name, StringComparer.Ordinal) ||
    method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName is
        "HarmonyLib.HarmonyPrefix" or
        "HarmonyLib.HarmonyPostfix" or
        "HarmonyLib.HarmonyTranspiler" or
        "HarmonyLib.HarmonyFinalizer");

static IEnumerable<MethodDefinition> GetReachableLocalMethods(
    MethodDefinition method,
    ModuleDefinition module,
    HashSet<MethodDefinition> visited)
{
    if (!method.HasBody || !visited.Add(method))
        yield break;

    yield return method;

    foreach (Instruction instruction in method.Body.Instructions)
    {
        if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt) ||
            instruction.Operand is not MethodReference called)
        {
            continue;
        }

        MethodDefinition? resolved;
        try
        {
            resolved = called.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            continue;
        }

        if (resolved?.Module != module)
            continue;

        foreach (MethodDefinition reachable in GetReachableLocalMethods(resolved, module, visited))
            yield return reachable;
    }
}

static IEnumerable<TypeDefinition> GetAllTypes(IEnumerable<TypeDefinition> roots)
{
    foreach (TypeDefinition type in roots)
    {
        yield return type;
        foreach (TypeDefinition nested in GetAllTypes(type.NestedTypes))
            yield return nested;
    }
}

internal sealed record PatchTarget(TypeReference Type, string MethodName);
