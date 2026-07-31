using System.Reflection;

namespace PowerLease.Cli;

public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out);

    /// <summary>
    /// The command, writing to <paramref name="output" />.
    /// <para>
    /// Taking the writer as an argument rather than reaching for <see cref="Console.Out" /> is what lets a test
    /// capture the output without swapping a process-wide static -- which is only safe while this assembly holds
    /// a single test class, and stops being safe the moment it holds two.
    /// </para>
    /// </summary>
    public static int Run(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (args.Length == 0 || (args.Length == 1 && args[0] == "--version"))
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";

            output.WriteLine($"PowerLease CLI {version}");
            return 0;
        }

        output.WriteLine("Unsupported arguments. Use --version.");
        return 2;
    }
}
