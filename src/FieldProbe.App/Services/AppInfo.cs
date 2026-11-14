using System.Reflection;

namespace FieldProbe.App.Services;

public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "1.0.0";

    public static string Copyright { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
}
