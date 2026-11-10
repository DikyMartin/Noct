using System.Reflection;
using System.Text;

namespace Noct.Diagnostics;

/// <summary>
/// Reflection dump used by the Phase 0/2 dependency spike. The project rules forbid
/// writing code against an assumed library API, so the real member list is printed
/// from the pinned assemblies and kept as evidence in docs/BUILD_NOTES.md.
/// </summary>
internal static class ApiDump
{
    public static int Run(string[] filters)
    {
        var assemblies = new[] { "NAudio.Core", "NAudio.Wasapi", "NAudio.Asio" }
            .Select(n => Assembly.Load(n))
            .ToArray();

        var sb = new StringBuilder();
        foreach (var asm in assemblies.Distinct())
        {
            sb.AppendLine($"=== {asm.GetName().Name} {asm.GetName().Version} ===");
            foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var name = t.FullName ?? t.Name;
                if (filters.Length > 0 && !filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                DescribeType(sb, t, detailed: filters.Length > 0);
            }
        }

        Console.Out.Write(sb.ToString());
        return 0;
    }

    private static void DescribeType(StringBuilder sb, Type t, bool detailed)
    {
        var kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class";
        sb.AppendLine($"\n{kind} {t.FullName}");
        if (!detailed)
        {
            return;
        }

        if (t.IsEnum)
        {
            foreach (var v in Enum.GetValues(t))
            {
                sb.AppendLine($"    {v} = {Convert.ToInt64(v)}");
            }

            return;
        }

        foreach (var c in t.GetConstructors())
        {
            sb.AppendLine($"    ctor({Params(c)})");
        }

        foreach (var p in t.GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            sb.AppendLine($"    prop {Short(p.PropertyType)} {p.Name} {{{(p.CanRead ? " get;" : "")}{(p.CanWrite ? " set;" : "")} }}");
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            sb.AppendLine($"    meth {Short(m.ReturnType)} {m.Name}({Params(m)})");
        }

        foreach (var e in t.GetEvents())
        {
            sb.AppendLine($"    event {Short(e.EventHandlerType!)} {e.Name}");
        }

        foreach (var f in t.GetFields(flags).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            sb.AppendLine($"    field {Short(f.FieldType)} {f.Name}");
        }
    }

    private static string Params(MethodBase m) =>
        string.Join(", ", m.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"));

    private static string Short(Type t) => t.IsGenericType
        ? $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(Short))}>"
        : t.Name;
}
