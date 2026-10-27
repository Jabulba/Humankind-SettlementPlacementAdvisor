using System;

// Command-line face of GameBuildReader: prints major.minor.revision.build on success, the reason
// on stderr with exit 1, and usage with exit 2. The plugin build's GenerateModVersion target runs
// this against the Assembly-CSharp.dll its compile is given.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: GameBuild <game-assembly.dll>");
            return 2;
        }

        string build;
        string failure;
        if (!GameBuildReader.TryRead(args[0], out build, out failure))
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        Console.WriteLine(build);
        return 0;
    }
}
