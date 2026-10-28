using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     Decode boundaries of the build-time version reader (`GameBuildReader`), measured on
	///     synthetic assemblies: the stamp is the integral named arguments Major, Minor, Revision and
	///     Build of the first assembly-level attribute named VersionAttribute, so every rule the
	///     reader's contract states has a row here. The real Assembly-CSharp.dll carries the single
	///     shape the current game ships, which GameBuildReaderTests pins; these rows pin the rest.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Expected stamps and failure texts are literals derived from the reader's contract
	///         comment, never read back from the code under test.
	///     </para>
	///     <para>
	///         Every row emits its own assembly identity: Assembly.LoadFrom dedupes by identity name,
	///         so rows sharing a name would read an earlier row's assembly. Each row's assembly lives
	///         beside the synthetic attribute assembly, which is the sibling layout both reference
	///         modes ship.
	///     </para>
	/// </remarks>
	public class GameBuildReaderDecodeTests : IDisposable
	{
		// Attribute shapes the reader must answer: the shipped shape (Label string, Accessibility enum,
		// a member outside the four), narrow integral members (the shipped segment type), a long
		// segment, a member the game renamed away, a UInt32 segment above Int32 range, the same type
		// name in a foreign namespace, and a copy usable on a type for the assembly-scope rule.
		private const string AttributesSource = @"
namespace Amplitude.Framework
{
	[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
	public sealed class VersionAttribute : System.Attribute
	{
		public int Major { get; set; }
		public int Minor { get; set; }
		public int Revision { get; set; }
		public int Build { get; set; }
		public string Label { get; set; }
		public Accessibility Accessibility { get; set; }
		public int Foo { get; set; }
	}

	public enum Accessibility
	{
		Public
	}
}

namespace Narrow
{
	[System.AttributeUsage(System.AttributeTargets.Assembly)]
	public sealed class VersionAttribute : System.Attribute
	{
		public short Major { get; set; }
		public short Minor { get; set; }
		public short Revision { get; set; }
		public short Build { get; set; }
	}
}

namespace Loose
{
	[System.AttributeUsage(System.AttributeTargets.Assembly)]
	public sealed class VersionAttribute : System.Attribute
	{
		public long Major { get; set; }
		public int Minor { get; set; }
		public int Revision { get; set; }
		public int Build { get; set; }
	}
}

namespace MissingBuild
{
	[System.AttributeUsage(System.AttributeTargets.Assembly)]
	public sealed class VersionAttribute : System.Attribute
	{
		public int Major { get; set; }
		public int Minor { get; set; }
		public int Revision { get; set; }
	}
}

namespace WideBuild
{
	[System.AttributeUsage(System.AttributeTargets.Assembly)]
	public sealed class VersionAttribute : System.Attribute
	{
		public int Major { get; set; }
		public int Minor { get; set; }
		public int Revision { get; set; }
		public uint Build { get; set; }
	}
}

namespace Decoy
{
	[System.AttributeUsage(System.AttributeTargets.Assembly)]
	public sealed class VersionAttribute : System.Attribute
	{
		public int Major { get; set; }
		public int Minor { get; set; }
		public int Revision { get; set; }
		public int Build { get; set; }
	}
}

namespace TypeScoped
{
	[System.AttributeUsage(System.AttributeTargets.All)]
	public sealed class VersionAttribute : System.Attribute
	{
		public int Major { get; set; }
		public int Minor { get; set; }
		public int Revision { get; set; }
		public int Build { get; set; }
	}
}
";
		private readonly string _root;
		private readonly string _attributesPath;
		private readonly List<MetadataReference> _frameworkReferences;
		private readonly string _salt = Guid.NewGuid().ToString("N").Substring(0, 8);
		private int _subject;

		public GameBuildReaderDecodeTests()
		{
			_root = Path.Combine(Path.GetTempPath(), "spa-gamebuild-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
			_frameworkReferences = FrameworkReferences();
			_attributesPath = Emit("SpaSyntheticAttributes", AttributesSource);
		}

		public void Dispose()
		{
			try
			{
				Directory.Delete(_root, true);
			}
			catch (IOException)
			{
				// A loaded assembly keeps its image open on some hosts; the temp tree is throwaway.
			}
		}

		[Fact]
		public void FourIntegralSegmentsBecomeTheStamp()
		{
			string path = EmitSubject("[assembly: Amplitude.Framework.VersionAttribute(Major = 1, Minor = 2, Revision = 3, Build = 4)]");

			string build;
			string failure;

			Assert.True(GameBuildReader.TryRead(path, out build, out failure), failure);
			Assert.Equal("1.2.3.4", build);
		}

		[Fact]
		public void NarrowIntegralSegmentsStampTheSame()
		{
			// The shipped shape: Major, Minor and Revision arrive as Int16, Build as Int32.
			string path = EmitSubject("[assembly: Narrow.VersionAttribute(Major = 1, Minor = 2, Revision = 3, Build = 4)]");

			string build;
			string failure;

			Assert.True(GameBuildReader.TryRead(path, out build, out failure), failure);
			Assert.Equal("1.2.3.4", build);
		}

		[Fact]
		public void MembersOutsideTheFourSegmentsAreSkipped()
		{
			string path = EmitSubject(
				"[assembly: Amplitude.Framework.VersionAttribute(Major = 1, Minor = 2, Revision = 3, Build = 4, Label = \"RELEASE\", Accessibility = Amplitude.Framework.Accessibility.Public, Foo = 7)]");

			string build;
			string failure;

			Assert.True(GameBuildReader.TryRead(path, out build, out failure), failure);
			Assert.Equal("1.2.3.4", build);
		}

		[Fact]
		public void AnAllZeroAttributeIsAFailureNotABuild()
		{
			string path = EmitSubject("[assembly: Amplitude.Framework.VersionAttribute(Major = 0, Minor = 0, Revision = 0, Build = 0)]");

			string build;
			string failure;

			Assert.False(GameBuildReader.TryRead(path, out build, out failure));
			AssertFailure(path, build, failure, "0.0.0.0");
		}

		[Fact]
		public void AMissingSegmentFailsNamingIt()
		{
			// The game renamed Build away: no integral segment, so no stamp with a zero tail.
			string path = EmitSubject("[assembly: MissingBuild.VersionAttribute(Major = 1, Minor = 2, Revision = 3)]");

			string build;
			string failure;

			Assert.False(GameBuildReader.TryRead(path, out build, out failure));
			AssertFailure(path, build, failure, "Build");
		}

		[Fact]
		public void ANonIntegralSegmentFailsNamingIt()
		{
			// Major moved to a non-integral member type: skipped by the integral filter, so missing.
			string path = EmitSubject("[assembly: Loose.VersionAttribute(Major = 1, Minor = 2, Revision = 3, Build = 4)]");

			string build;
			string failure;

			Assert.False(GameBuildReader.TryRead(path, out build, out failure));
			AssertFailure(path, build, failure, "Major");
		}

		[Fact]
		public void ASegmentAboveInt32RangeFails()
		{
			string path = EmitSubject("[assembly: WideBuild.VersionAttribute(Major = 1, Minor = 2, Revision = 3, Build = 4294967295)]");

			string build;
			string failure;

			Assert.False(GameBuildReader.TryRead(path, out build, out failure));
			AssertFailure(path, build, failure, "Overflow");
		}

		[Fact]
		public void TheFirstAssemblyLevelMatchDecides()
		{
			string path = EmitSubject(
				"[assembly: Amplitude.Framework.VersionAttribute(Major = 1, Minor = 1, Revision = 1, Build = 1)]\n" +
				"[assembly: Amplitude.Framework.VersionAttribute(Major = 2, Minor = 2, Revision = 2, Build = 2)]");

			string build;
			string failure;

			Assert.True(GameBuildReader.TryRead(path, out build, out failure), failure);
			Assert.Equal("1.1.1.1", build);
		}

		[Fact]
		public void TheFirstMatchDecidesEvenWhenAUsableOneFollows()
		{
			string path = EmitSubject(
				"[assembly: Amplitude.Framework.VersionAttribute(Major = 0, Minor = 0, Revision = 0, Build = 0)]\n" +
				"[assembly: Amplitude.Framework.VersionAttribute(Major = 2, Minor = 2, Revision = 2, Build = 2)]");

			string build;
			string failure;

			Assert.False(GameBuildReader.TryRead(path, out build, out failure));
			AssertFailure(path, build, failure, "0.0.0.0");
		}

		[Fact]
		public void TheMatchIsOnTheTypeSoANamespaceMoveIsRead()
		{
			string path = EmitSubject("[assembly: Decoy.VersionAttribute(Major = 9, Minor = 8, Revision = 7, Build = 6)]");

			string build;
			string failure;

			Assert.True(GameBuildReader.TryRead(path, out build, out failure), failure);
			Assert.Equal("9.8.7.6", build);
		}

		[Fact]
		public void TheNetAssemblyVersionAttributeIsNotTheBuild()
		{
			string path = EmitSubject("[assembly: System.Reflection.AssemblyVersion(\"9.9.9.9\")]");

			string build;
			string failure;

			Assert.False(GameBuildReader.TryRead(path, out build, out failure));
			AssertFailure(path, build, failure, "no assembly-level Version attribute");
		}

		[Fact]
		public void TheSameAttributeOnATypeIsNotABuild()
		{
			string path = EmitSubject(
				"[TypeScoped.VersionAttribute(Major = 1, Minor = 2, Revision = 3, Build = 4)]\ninternal sealed class Subject2 { }");

			string build;
			string failure;

			Assert.False(GameBuildReader.TryRead(path, out build, out failure));
			AssertFailure(path, build, failure, "no assembly-level Version attribute");
		}

		// The reader's failure names the file, then the reason. Asserted on the tail: a wrong reason
		// shows the whole text, which a Contains on a long string truncates away.
		private static void AssertFailure(string path, string build, string failure, string reason)
		{
			Assert.Null(build);
			Assert.StartsWith(path, failure);
			Assert.True(failure.Contains(reason), failure);
		}

		// xunit builds a fixture per row, and AssemblyLoadContext keys on assembly identity: every
		// row's subject needs its own identity name, so a later LoadFrom never hands back an earlier
		// row's loaded assembly.
		private string EmitSubject(string attributes)
		{
			_subject++;
			string source = attributes + "\ninternal sealed class Subject { }\n";
			return Emit("SpaSyntheticSubject" + _salt + _subject.ToString("D3"), source);
		}

		private string Emit(string assemblyName, string source)
		{
			List<MetadataReference> references = new List<MetadataReference>(_frameworkReferences);
			if (assemblyName != "SpaSyntheticAttributes")
			{
				references.Add(MetadataReference.CreateFromFile(_attributesPath));
			}

			CSharpCompilation compilation = CSharpCompilation.Create(
				assemblyName,
				new SyntaxTree[] { CSharpSyntaxTree.ParseText(source) },
				references,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

			using (MemoryStream stream = new MemoryStream())
			{
				EmitResult result = compilation.Emit(stream);
				Assert.True(
					result.Success,
					string.Join(" | ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));

				string path = Path.Combine(_root, assemblyName + ".dll");
				File.WriteAllBytes(path, stream.ToArray());
				return path;
			}
		}

		// In-proc compilation needs the reference assemblies the test host runs on: the shared
		// framework directory of the running runtime.
		private static List<MetadataReference> FrameworkReferences()
		{
			string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
			List<MetadataReference> references = new List<MetadataReference>();
			foreach (string candidate in Directory.EnumerateFiles(runtimeDirectory, "*.dll").OrderBy(path => path))
			{
				references.Add(MetadataReference.CreateFromFile(candidate));
			}

			return references;
		}
	}
}
