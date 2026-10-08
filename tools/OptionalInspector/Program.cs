using System;
using System.Reflection;
using System.Linq;
using System.IO;
using System.Runtime.Loader;
// Metadata inspection only: this tool never initializes Core or calls trading/data APIs.
var sdkDirectory = Environment.GetEnvironmentVariable("QUANTOWER_SDK_PATH");
if (args.Length >= 2 && args[0] == "--sdk") { sdkDirectory = args[1]; args = args.Skip(2).ToArray(); }
if (string.IsNullOrWhiteSpace(sdkDirectory)) throw new ArgumentException("Pass --sdk <installed version/bin directory> or set QUANTOWER_SDK_PATH.");
if (args.Length > 0 && args[0] == "--metadata-il") { MetadataIl.Print(Path.Combine(sdkDirectory, "TradingPlatform.BusinessLayer.dll"), args.Skip(1).ToArray()); return; }
AssemblyLoadContext.Default.Resolving += (context, name) => {
    var path = Path.Combine(sdkDirectory, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(sdkDirectory, "TradingPlatform.BusinessLayer.dll"));
foreach (var name in args.Length == 0 ? new[] { "Core", "Symbol", "OptionSerie", "GetOptionSeriesRequestParameters", "GetStrikesRequestParameters", "VolumeAnalysisCalculationParameters", "VolumeAnalysisCalculationRequest", "IVolumeAnalysisCalculationProgress", "VolumeAnalysisData", "VolumeAnalysisItem", "VolumeAnalysisMetadata", "HistoricalData", "HistoryRequestParameters", "DayBar", "OptionType" } : args)
{
    var type = assembly.GetTypes().FirstOrDefault(t => t.Name == name);
    Console.WriteLine("TYPE " + name + " " + type);
    if (type == null) continue;
    foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OrderBy(m => m.Name))
    {
        if (member is MethodInfo m && m.IsSpecialName) continue;
        Console.WriteLine(member.MemberType + " " + member);
        if (member is MethodInfo method) foreach(var parameter in method.GetParameters()) Console.WriteLine(" PARAM " + parameter.Name + " default=" + parameter.DefaultValue);
        if (member is PropertyInfo property) Console.WriteLine(" GET=" + property.CanRead + " SET=" + property.CanWrite);
        if (member is EventInfo ev) Console.WriteLine(" INVOKE=" + ev.EventHandlerType.GetMethod("Invoke"));
    }
}
