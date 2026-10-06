using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;
if (args.Length != 2) throw new ArgumentException("Usage: registration-patch runtime-directory repair-assembly");
var runtime = args[0];
var path = Path.Combine(runtime, "PTGOilSystem.Web.dll");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(runtime);
resolver.AddSearchDirectory(Path.GetDirectoryName(args[1])!);
Console.WriteLine("Read installed assembly");
using var main = AssemblyDefinition.ReadAssembly(path, new ReaderParameters {
    AssemblyResolver = resolver, ReadSymbols = true, InMemory = true });
if (main.Name.HasPublicKey) throw new Exception("Refuse signed assembly");
using var repair = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
var replacement = repair.MainModule.Types.Single(t => t.FullName == "PTG.ContractReportingRepair.DirectTransportSalesAccountingAdapter");
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) {
    foreach (var t in types) { yield return t; foreach(var child in Types(t.NestedTypes)) yield return child; }
}
string Body(MethodDefinition m) {
    if (!m.HasBody) return "";
    var indexes = m.Body.Instructions.Select((instruction,index) => (instruction,index)).ToDictionary(x => x.instruction,x => x.index);
    string Op(object? op) => op switch {
        null => "", Instruction i => "@" + indexes[i],
        Instruction[] list => string.Join(",",list.Select(i => "@" + indexes[i])),
        MemberReference member => member.FullName,
        ParameterDefinition p => "P" + p.Index,
        VariableDefinition v => "V" + v.Index,
        _ => op.ToString() ?? ""
    };
    var canonical = string.Join("\n",m.Body.Instructions.Select(i => i.OpCode.Name + " " + Op(i.Operand)))
        + "\nEH=" + string.Join(";",m.Body.ExceptionHandlers.Select(e => e.HandlerType + ":" + Op(e.TryStart)
            + ":" + Op(e.TryEnd) + ":" + Op(e.HandlerStart) + ":" + Op(e.HandlerEnd) + ":" + Op(e.FilterStart)
            + ":" + e.CatchType?.FullName));
    return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
}
Dictionary<string,string> Bodies(AssemblyDefinition a) => Types(a.MainModule.Types)
    .SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Body);
Console.WriteLine("Hash original methods");
var before = Bodies(main);
Console.WriteLine("Hashed " + before.Count + " methods");
var resources = main.MainModule.Resources.OfType<EmbeddedResource>().ToDictionary(r => r.Name,
    r => Convert.ToHexString(SHA256.HashData(r.GetResourceData())));
var targets = Types(main.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
    .SelectMany(m => m.Body.Instructions.Select(i => (Method:m, Instruction:i)))
    .Where(x => x.Instruction.Operand is GenericInstanceMethod method && method.Name == "AddScoped"
        && method.GenericArguments.Count == 2
        && method.GenericArguments[0].FullName == "PTGOilSystem.Web.Services.Accounting.ISalesAccountingAdapter"
        && method.GenericArguments[1].FullName == "PTGOilSystem.Web.Services.Accounting.SalesAccountingAdapter").ToList();
if (targets.Count != 1) throw new Exception("Expected exactly one original sales adapter registration");
var target = targets[0];
((GenericInstanceMethod)target.Instruction.Operand).GenericArguments[1] = main.MainModule.ImportReference(replacement);
Console.WriteLine("Hash expected registration change");
var expected = Bodies(main);
var temp = Path.Combine(runtime, "registration-patched.dll");
Console.WriteLine("Write patched assembly");
main.Write(temp, new WriterParameters { WriteSymbols = true });
Console.WriteLine("Verify round-trip");
using var checkedMain = AssemblyDefinition.ReadAssembly(temp, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = true });
var after = Bodies(checkedMain);
if (after.Count != before.Count || after.Any(x => expected[x.Key] != x.Value))
    throw new Exception("Round-trip changed unexpected IL");
var changed = after.Keys.Where(k => before[k] != after[k]).ToList();
if (changed.Count != 1 || changed[0] != target.Method.FullName) throw new Exception("Unexpected changed method");
foreach (var resource in checkedMain.MainModule.Resources.OfType<EmbeddedResource>())
    if (resources[resource.Name] != Convert.ToHexString(SHA256.HashData(resource.GetResourceData())))
        throw new Exception("Embedded resource changed");
var metadata = new {
    ChangedMethod = changed[0], ChangedInstruction = "AddScoped<ISalesAccountingAdapter,DirectTransportSalesAccountingAdapter>",
    MethodsVerified = before.Count, EmbeddedResourcesVerified = resources.Count,
    OriginalSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
    PatchedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(temp)))
};
File.Copy(temp,path,true);
File.Copy(Path.ChangeExtension(temp,"pdb"),Path.ChangeExtension(path,"pdb"),true);
File.Delete(temp); File.Delete(Path.ChangeExtension(temp,"pdb"));
File.WriteAllText(Path.Combine(runtime,"direct-cogs-registration-proof.json"),JsonSerializer.Serialize(metadata,new JsonSerializerOptions{WriteIndented=true}) + "\n");
Console.WriteLine(JsonSerializer.Serialize(metadata));
