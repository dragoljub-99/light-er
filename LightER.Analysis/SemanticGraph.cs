using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using LightER.Analysis.Dtos;

namespace LightER.Analysis
{
    public static class SemanticGraph
    {
        public static GraphDto ScanGraphSemantic(IEnumerable<string> csFilePaths)
        {
            var paths = csFilePaths.ToArray();
            var build = CompilationBuilder.BuildCompilation(paths);
            var comp = build.Compilation;

            var types = new List<TypeInfoDto>();
            var decls = new List<(BaseTypeDeclarationSyntax Node, string File)>();
            var symbolToId = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);

            foreach (var path in paths)
            {
                if (!build.TreesByPath.TryGetValue(path, out var tree)) continue;
                var model = comp.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (node.Parent is BaseTypeDeclarationSyntax) continue;

                    var sym = model.GetDeclaredSymbol(node) as INamedTypeSymbol;
                    if (sym is null) continue;

                    var id = GetQualifiedName(sym);
                    var kind = MapKind(sym);

                    if (!symbolToId.ContainsKey(sym))
                        symbolToId[sym] = id;

                    if (!types.Any(t => t.Id == id))
                    {
                        types.Add(new TypeInfoDto
                        {
                            Id = id,
                            Name = sym.Name,
                            Namespace = sym.ContainingNamespace?.ToDisplayString() ?? "",
                            Kind = kind,
                            File = Path.GetFileName(path),
                            Inherits = new List<string>(),
                            Implements = new List<string>(),
                            Uses = new List<UseRefDto>(),
                            UsedBy = new List<string>()
                        });
                    }

                    decls.Add((node, path));
                }
            }

            var byId = types.ToDictionary(t => t.Id, t => t, StringComparer.Ordinal);
            var idsSet = new HashSet<string>(byId.Keys, StringComparer.Ordinal);

            bool IsUserType(INamedTypeSymbol s)
            {
                return s.Locations.Any(l => l.IsInSource);
            }

            var edges = new HashSet<EdgeDto>(new EdgeComparer());

            foreach (var (node, filePath) in decls)
            {
                if (!build.TreesByPath.TryGetValue(filePath, out var tree)) continue;
                var model = comp.GetSemanticModel(tree);

                var meSym = model.GetDeclaredSymbol(node) as INamedTypeSymbol;
                if (meSym is null) continue;

                var meId = GetQualifiedName(meSym);
                if (!byId.TryGetValue(meId, out var me)) continue;

                var baseTypes = GetBaseTypeSymbols(node, model);
                if (node is ClassDeclarationSyntax or RecordDeclarationSyntax)
                {
                    if (baseTypes.Count > 0)
                    {
                        var first = baseTypes[0];
                        if (first is not null && IsUserType(first))
                        {
                            var tId = GetQualifiedName(first);
                            me.Inherits.Add(tId);
                            edges.Add(new EdgeDto { From = meId, To = tId, Rel = "inherits" });
                        }

                        for (int i = 1; i < baseTypes.Count; i++)
                        {
                            var iface = baseTypes[i];
                            if (iface is not null && IsUserType(iface))
                            {
                                var tId = GetQualifiedName(iface);
                                me.Implements.Add(tId);
                                edges.Add(new EdgeDto { From = meId, To = tId, Rel = "implements" });
                            }
                        }
                    }
                }
                else if (node is StructDeclarationSyntax)
                {
                    foreach (var iface in baseTypes)
                    {
                        if (iface is not null && IsUserType(iface))
                        {
                            var tId = GetQualifiedName(iface);
                            me.Implements.Add(tId);
                            edges.Add(new EdgeDto { From = meId, To = tId, Rel = "implements" });
                        }
                    }
                }
                else if (node is InterfaceDeclarationSyntax)
                {
                    foreach (var parent in baseTypes)
                    {
                        if (parent is not null && IsUserType(parent))
                        {
                            var tId = GetQualifiedName(parent);
                            me.Inherits.Add(tId);
                            edges.Add(new EdgeDto { From = meId, To = tId, Rel = "inherits" });
                        }
                    }
                }

                foreach (var member in GetMembers(node))
                {
                    if (member is FieldDeclarationSyntax f)
                    {
                        var typeSym = model.GetTypeInfo(f.Declaration.Type).Type;
                        foreach (var u in CandidateUserTypeSymbols(typeSym))
                        {
                            if (!IsUserType(u)) continue;
                            var tId = GetQualifiedName(u);

                            foreach (var v in f.Declaration.Variables)
                            {
                                var memberName = v.Identifier.Text;
                                me.Uses.Add(new UseRefDto { Target = tId, Via = "field", Member = memberName });
                                edges.Add(new EdgeDto { From = meId, To = tId, Rel = "field" });
                            }
                        }
                    }
                    else if (member is PropertyDeclarationSyntax p)
                    {
                        var typeSym = model.GetTypeInfo(p.Type).Type;
                        foreach (var u in CandidateUserTypeSymbols(typeSym))
                        {
                            if (!IsUserType(u)) continue;
                            var tId = GetQualifiedName(u);
                            me.Uses.Add(new UseRefDto { Target = tId, Via = "property", Member = p.Identifier.Text });
                            edges.Add(new EdgeDto { From = meId, To = tId, Rel = "property" });
                        }
                    }
                }
            }

            foreach (var e in edges)
            {
                if (byId.TryGetValue(e.To, out var target))
                {
                    if (!target.UsedBy.Contains(e.From))
                        target.UsedBy.Add(e.From);
                }
            }

            foreach (var t in types)
            {
                t.Inherits = t.Inherits.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                t.Implements = t.Implements.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                t.Uses = t.Uses
                    .OrderBy(u => u.Target, StringComparer.Ordinal)
                    .ThenBy(u => u.Via, StringComparer.Ordinal)
                    .ThenBy(u => u.Member, StringComparer.Ordinal)
                    .ToList();
                t.UsedBy = t.UsedBy.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            }

            var edgeList = edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Rel, StringComparer.Ordinal)
                .ToList();

            return new GraphDto
            {
                Types = types
                    .OrderBy(t => t.Namespace, StringComparer.Ordinal)
                    .ThenBy(t => t.Name, StringComparer.Ordinal)
                    .ToList(),
                Edges = edgeList
            };
        }
        private static string MapKind(INamedTypeSymbol s) =>
            s.TypeKind switch
            {
                TypeKind.Class => "class",
                TypeKind.Struct => "struct",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                _ => "type"
            };

        private static string GetQualifiedName(INamedTypeSymbol s)
        {
            var nameParts = new List<string>();
            for (var cur = s; cur is not null; cur = cur.ContainingType)
                nameParts.Insert(0, cur.Name);

            var ns = s.ContainingNamespace?.ToDisplayString();
            return string.IsNullOrEmpty(ns)
                ? string.Join(".", nameParts)
                : $"{ns}.{string.Join(".", nameParts)}";
        }
        private static IEnumerable<MemberDeclarationSyntax> GetMembers(BaseTypeDeclarationSyntax node) =>
            node switch
            {
                ClassDeclarationSyntax c => c.Members,
                StructDeclarationSyntax s => s.Members,
                InterfaceDeclarationSyntax i => i.Members,
                RecordDeclarationSyntax r => r.Members,
                EnumDeclarationSyntax _ => Array.Empty<MemberDeclarationSyntax>(),
                _ => Array.Empty<MemberDeclarationSyntax>()
            };

        private static List<INamedTypeSymbol?> GetBaseTypeSymbols(BaseTypeDeclarationSyntax node, SemanticModel model)
        {
            var list = new List<INamedTypeSymbol?>();
            BaseListSyntax? baseList = node switch
            {
                ClassDeclarationSyntax c => c.BaseList,
                StructDeclarationSyntax s => s.BaseList,
                InterfaceDeclarationSyntax i => i.BaseList,
                RecordDeclarationSyntax r => r.BaseList,
                _ => null
            };
            if (baseList is null) return list;

            foreach (var t in baseList.Types)
            {
                var info = model.GetTypeInfo(t.Type);
                list.Add(info.Type as INamedTypeSymbol);
            }
            return list;
        }

        private static IEnumerable<INamedTypeSymbol> CandidateUserTypeSymbols(ITypeSymbol? type)
        {
            if (type is null) yield break;

            switch (type)
            {
                case IArrayTypeSymbol arr:
                    foreach (var inner in CandidateUserTypeSymbols(arr.ElementType)) yield return inner;
                    yield break;

                case IPointerTypeSymbol ptr:
                    foreach (var inner in CandidateUserTypeSymbols(ptr.PointedAtType)) yield return inner;
                    yield break;

                case INamedTypeSymbol named:
                    if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && named.TypeArguments.Length == 1)
                    {
                        foreach (var inner in CandidateUserTypeSymbols(named.TypeArguments[0])) yield return inner;
                        yield break;
                    }

                    if (named.IsGenericType && named.TypeArguments.Length > 0)
                    {
                        foreach (var arg in named.TypeArguments)
                            foreach (var inner in CandidateUserTypeSymbols(arg))
                                yield return inner;
                        yield break;
                    }

                    yield return named;
                    yield break;

                default:
                    yield break;
            }
        }
    }
}