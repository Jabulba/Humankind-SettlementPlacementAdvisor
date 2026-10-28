using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;

/// <summary>
///     Reads the game build from the assembly-level Version attribute of a game assembly: the
///     number the game itself reads at runtime (Amplitude.Framework.Application
///     LoadVersionFromAssemblyAttribute) and the number GameApi.GameBuild reports in a load failure
///     line, so a stamped build and a reported build are the same kind of fact.
/// </summary>
/// <remarks>
///     <para>
///         MetadataLoadContext reads the attribute straight out of the metadata: no assembly is
///         loaded into the process, so no module initializer runs and no game code executes, and a
///         damaged image is a failure the caller reads instead of a crash inside the caller.
///     </para>
///     <para>
///         Resolution is scoped to the directory of the read assembly, which is where the
///         attribute's declaring assembly sits beside it in both reference modes. The net472 core a
///         game assembly points at is answered by the core of the runtime running the read, since no
///         game directory ships System.Private.CoreLib and no .NET Core output ships mscorlib; a
///         name outside that directory is a failure, never a probe of the machine.
///     </para>
///     <para>
///         Matching is on the attribute type's name, namespace-free, so a game that moves the
///         attribute to another namespace is still read. The first assembly-level match decides; the
///         scan never looks past it, and the same attribute on a type is not a build.
///     </para>
///     <para>
///         The four segments arrive as integral named arguments (Build Int32, Major, Minor and
///         Revision Int16 on the current game) while Label is a string and Accessibility an enum:
///         only integral named arguments are read, so a future string or enum member is skipped.
///         Success needs all four segments recognized, so a member renamed away fails naming the
///         member, never a stamp with a zero segment. An all-zero result is a failure too: the game's
///         own static constructor stamps all zeros before it loads the attribute, and the mod reports
///         that as the unknown build.
///     </para>
///     <para>
///         The producer's Humankind-GameLibs derive tool reads the same attribute with the same
///         decoder, so a staged set and a resolved reference set report one number.
///     </para>
/// </remarks>
public static class GameBuildReader
{
    /// <summary>
    ///     Reads major.minor.revision.build from the Version attribute. Success needs the attribute,
    ///     four recognized integral segments, and at least one nonzero segment: the game's own static
    ///     constructor stamps all zeros before it loads the attribute, which the mod reports as the
    ///     unknown build. On failure the reason names the file.
    /// </summary>
    public static bool TryRead(string assemblyPath, out string build, out string failure)
    {
        build = null;
        failure = null;

        string path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path))
        {
            failure = "file not found: " + path;
            return false;
        }

        string directory = Path.GetDirectoryName(path);
        try
        {
            using (MetadataLoadContext context = new MetadataLoadContext(
                new DirectoryAssemblyResolver(Directory.EnumerateFiles(directory, "*.dll")), "System.Private.CoreLib"))
            {
                Assembly assembly = context.LoadFromAssemblyPath(path);
                foreach (CustomAttributeData data in assembly.GetCustomAttributesData())
                {
                    if (data.AttributeType.Name != "VersionAttribute")
                    {
                        continue;
                    }

                    int major = 0;
                    int minor = 0;
                    int revision = 0;
                    int buildNumber = 0;
                    bool majorSet = false;
                    bool minorSet = false;
                    bool revisionSet = false;
                    bool buildSet = false;
                    foreach (CustomAttributeNamedArgument named in data.NamedArguments)
                    {
                        object raw = named.TypedValue.Value;
                        switch (Convert.GetTypeCode(raw))
                        {
                            case TypeCode.Byte:
                            case TypeCode.Int16:
                            case TypeCode.Int32:
                            case TypeCode.SByte:
                            case TypeCode.UInt16:
                            case TypeCode.UInt32:
                                break;
                            default:
                                continue;
                        }

                        int value = Convert.ToInt32(raw);
                        switch (named.MemberName)
                        {
                            case "Major": major = value; majorSet = true; break;
                            case "Minor": minor = value; minorSet = true; break;
                            case "Revision": revision = value; revisionSet = true; break;
                            case "Build": buildNumber = value; buildSet = true; break;
                        }
                    }

                    string missing = MissingSegments(majorSet, minorSet, revisionSet, buildSet);
                    if (missing != null)
                    {
                        failure = path + ": the Version attribute has no integral segment for " + missing;
                        return false;
                    }

                    if (major == 0 && minor == 0 && revision == 0 && buildNumber == 0)
                    {
                        failure = path + ": the Version attribute reports 0.0.0.0, the game's unset state";
                        return false;
                    }

                    build = major + "." + minor + "." + revision + "." + buildNumber;
                    return true;
                }

                failure = path + ": no assembly-level Version attribute";
                return false;
            }
        }
        catch (Exception e)
        {
            failure = path + ": " + e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    // The segments with no recognized integral named argument, in stamp order, null when all four
    // are set: a member the game renamed away is a failure naming it, not a zero in the stamp.
    private static string MissingSegments(bool major, bool minor, bool revision, bool build)
    {
        List<string> missing = new List<string>();
        if (!major)
        {
            missing.Add("Major");
        }
        if (!minor)
        {
            missing.Add("Minor");
        }
        if (!revision)
        {
            missing.Add("Revision");
        }
        if (!build)
        {
            missing.Add("Build");
        }

        return missing.Count == 0 ? null : string.Join(", ", missing);
    }
}

/// <summary>
///     Metadata-only assembly resolution over one directory: first file wins per simple name, and
///     the net472 core a game assembly points at is answered by the core of the runtime running the
///     read, since no game directory ships System.Private.CoreLib and no .NET Core output ships
///     mscorlib. A name outside the directory is a failure, never a probe of the machine.
/// </summary>
public sealed class DirectoryAssemblyResolver : MetadataAssemblyResolver
{
    private static readonly string CoreAssemblyPath = typeof(object).Assembly.Location;

    private readonly Dictionary<string, string> _paths;

    public DirectoryAssemblyResolver(IEnumerable<string> candidatePaths)
    {
        _paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in candidatePaths)
        {
            string name = Path.GetFileNameWithoutExtension(candidate);
            if (!_paths.ContainsKey(name))
            {
                _paths.Add(name, candidate);
            }
        }
    }

    public override Assembly Resolve(MetadataLoadContext context, AssemblyName assemblyName)
    {
        // The core first, always the runtime's own: a game directory ships the net472 mscorlib, and
        // mixing that core with the runtime's core makes enum decoding throw, so the core of the
        // read is the core of the reader.
        if (assemblyName.Name == "mscorlib" || assemblyName.Name == "System.Private.CoreLib")
        {
            return context.LoadFromAssemblyPath(CoreAssemblyPath);
        }

        string path;
        if (_paths.TryGetValue(assemblyName.Name, out path))
        {
            return context.LoadFromAssemblyPath(path);
        }

        throw new FileNotFoundException("no assembly named " + assemblyName.Name + " beside the read assembly");
    }
}
