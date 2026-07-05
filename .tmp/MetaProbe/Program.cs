using System.Reflection;
using System.Runtime.Loader;

var hooks = @"C:\Users\egidi\AppData\Roaming\XIVLauncher\addon\Hooks\dev";
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(hooks, $"{name.Name}.dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};

var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hooks, "FFXIVClientStructs.dll"));
foreach (var type in asm.GetTypes().Where(t =>
             t.FullName?.StartsWith("FFXIVClientStructs.FFXIV.Client.UI.Info.CrossRealm", StringComparison.Ordinal) == true ||
             t.FullName == "FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCrossRealm"))
{
    Console.WriteLine($"TYPE {type.FullName}");
    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  PROP {property.Name}: {property.PropertyType.FullName}");
    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        Console.WriteLine($"  FIELD {field.Name}: {field.FieldType.FullName}");
}
