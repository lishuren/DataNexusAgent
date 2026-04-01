using System.Reflection;
var dll = Assembly.LoadFrom("/Users/sli3/.nuget/packages/microsoft.agents.ai.workflows/1.0.0-rc4/lib/net10.0/Microsoft.Agents.AI.Workflows.dll");
var type = dll.GetType("Microsoft.Agents.AI.Workflows.HandoffsWorkflowBuilder");
if (type == null) { Console.WriteLine("not found"); return; }
Console.WriteLine(type.FullName);
var baseType = type.BaseType!;
Console.WriteLine($"Base: {baseType.Name}");
foreach (var m in baseType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    Console.WriteLine("  " + m.Name);
