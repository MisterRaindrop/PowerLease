using System.Reflection;

namespace PowerLease.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0] == "--version"))
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";

            Console.WriteLine($"PowerLease CLI {version}");
            return 0;
        }

        Console.WriteLine("Unsupported arguments. Use --version.");
        return 2;
    }
}
