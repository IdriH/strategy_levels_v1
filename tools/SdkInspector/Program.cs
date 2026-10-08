using System.Reflection;
using System.Runtime.Loader;
var bin = args[0];
AssemblyLoadContext.Default.Resolving += (_, name) => { var path = Path.Combine(bin, name.Name + ".dll"); return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null; };
var sdk = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(bin, "TradingPlatform.BusinessLayer.dll"));
foreach (var name in args.Skip(1))
{
    var type = sdk.GetType(name) ?? sdk.GetTypes().FirstOrDefault(t => t.Name == name);
    Console.WriteLine($"TYPE {name}: {type}");
    if (type == null) continue;
    if (type.IsEnum) { Console.WriteLine(string.Join(", ", Enum.GetNames(type))); continue; }
    foreach(var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)) Console.WriteLine($" P {p.PropertyType} {p.Name} [{p.CanWrite}]");
    foreach(var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)) Console.WriteLine($" F {f.FieldType} {f.Name}");
    foreach(var c in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($" C {c}");
    foreach(var e in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)) Console.WriteLine($" E {e.Name}: {e.EventHandlerType?.GetMethod("Invoke")}");
    foreach(var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(m => !m.IsSpecialName && (m.IsPublic || m.IsFamily)).OrderBy(m => m.Name)) Console.WriteLine($" M {(m.IsPublic ? "public" : "protected")} {m}");
}
