using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LightER.Analysis
{
    public static class CompilationBuilder
    {
        public static CompilationBuildResult BuildCompilation(IEnumerable<string> csFilePaths)
        {
            var trees = new List<SyntaxTree>();

            foreach (var path in csFilePaths)
            {
                var text = File.ReadAllText(path);
                var tree = CSharpSyntaxTree.ParseText(text, path: path);
                trees.Add(tree);
            }

            var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;

            var references = tpa
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

            var compilation = CSharpCompilation.Create(
                assemblyName: "LightER_Analysis",
                syntaxTrees: trees,
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            var byPath = trees.ToDictionary(t => t.FilePath, t => t,
                                           StringComparer.OrdinalIgnoreCase);


            return new CompilationBuildResult(compilation, byPath);
        }
    }
    public sealed class CompilationBuildResult
    {
        public CSharpCompilation Compilation { get; }
        public IReadOnlyDictionary<string, SyntaxTree> TreesByPath { get; }

        public CompilationBuildResult(CSharpCompilation compilation, IReadOnlyDictionary<string, SyntaxTree> treesByPath)
        {
            Compilation = compilation;
            TreesByPath = treesByPath;
        }
    }
}