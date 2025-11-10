using System.Collections.Generic;
using LightER.Analysis.Dtos;

namespace LightER.Analysis
{
    public static class SemanticGraph
    {
        public static GraphDto ScanGraphSemantic(IEnumerable<string> csFilePaths)
        {
            var builder = CompilationBuilder.BuildCompilation(csFilePaths);

            return TypeScanner.ScanGraph(csFilePaths);
        }
    }
}