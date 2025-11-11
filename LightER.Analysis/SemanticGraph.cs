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
            public static GraphDto ScanGraphSemantic(IEnumerable<string> csFilePaths, bool includeMethods = false)
            {
                var paths = csFilePaths.ToArray();
                var build = CompilationBuilder.BuildCompilation(paths);
                var comp = build.Compilation;

                var typesById = new Dictionary<string, TypeInfoDto>(StringComparer.Ordinal);
                var decls = new List<(BaseTypeDeclarationSyntax Node, string File)>();
                var externalRefs = new HashSet<string>(StringComparer.Ordinal); 

                foreach (var path in paths)
                {
                    if (!build.TreesByPath.TryGetValue(path, out var tree)) continue;
                    var model = comp.GetSemanticModel(tree);
                    var root = tree.GetRoot();

                    foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                    {
                        var sym = model.GetDeclaredSymbol(node) as INamedTypeSymbol;
                        if (sym is null) continue;

                        var id = GetQualifiedName(sym);
                        var kind = MapKind(sym);

                        if (!typesById.TryGetValue(id, out var dto))
                        {
                            dto = new TypeInfoDto
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
                            };
                            typesById[id] = dto;
                        }

                        decls.Add((node, path));
                    }
                }

                bool IsUserType(INamedTypeSymbol s) => s.Locations.Any(l => l.IsInSource);

                var edges = new HashSet<EdgeDto>(new EdgeComparer());

                foreach (var (node, filePath) in decls)
                {
                    if (!build.TreesByPath.TryGetValue(filePath, out var tree)) continue;
                    var model = comp.GetSemanticModel(tree);
                    var meSym = model.GetDeclaredSymbol(node) as INamedTypeSymbol;
                    if (meSym is null) continue;

                    var meId = GetQualifiedName(meSym);
                    if (!typesById.TryGetValue(meId, out var me)) continue;

                    var baseTypes = GetBaseTypeSymbols(node, model);

                    if (node is ClassDeclarationSyntax or RecordDeclarationSyntax)
                    {
                        if (baseTypes.Count > 0)
                        {
                            var first = baseTypes[0];
                            if (first is not null)
                            {
                                if (IsUserType(first))
                                {
                                    var tId = GetQualifiedName(first);
                                    me.Inherits.Add(tId);
                                    edges.Add(new EdgeDto { From = meId, To = tId, Rel = "inherits" });
                                }
                                else externalRefs.Add(GetQualifiedName(first));
                            }
                            for (int i = 1; i < baseTypes.Count; i++)
                            {
                                var iface = baseTypes[i];
                                if (iface is null) continue;
                                if (IsUserType(iface))
                                {
                                    var tId = GetQualifiedName(iface);
                                    me.Implements.Add(tId);
                                    edges.Add(new EdgeDto { From = meId, To = tId, Rel = "implements" });
                                }
                                else externalRefs.Add(GetQualifiedName(iface));
                            }
                        }
                    }
                    else if (node is StructDeclarationSyntax)
                    {
                        foreach (var iface in baseTypes)
                        {
                            if (iface is null) continue;
                            if (IsUserType(iface))
                            {
                                var tId = GetQualifiedName(iface);
                                me.Implements.Add(tId);
                                edges.Add(new EdgeDto { From = meId, To = tId, Rel = "implements" });
                            }
                            else externalRefs.Add(GetQualifiedName(iface));
                        }
                    }
                    else if (node is InterfaceDeclarationSyntax)
                    {
                        foreach (var parent in baseTypes)
                        {
                            if (parent is null) continue;
                            if (IsUserType(parent))
                            {
                                var tId = GetQualifiedName(parent);
                                me.Inherits.Add(tId);
                                edges.Add(new EdgeDto { From = meId, To = tId, Rel = "inherits" });
                            }
                            else externalRefs.Add(GetQualifiedName(parent));
                        }
                    }

                    foreach (var member in GetMembers(node))
                    {
                        if (member is FieldDeclarationSyntax f)
                        {
                            var typeSym = model.GetTypeInfo(f.Declaration.Type).Type;
                            foreach (var u in CandidateUserTypeSymbols(typeSym))
                            {
                                if (u is null) continue;
                                var qn = GetQualifiedName(u);
                                if (IsUserType(u))
                                {
                                    foreach (var v in f.Declaration.Variables)
                                    {
                                        var memberName = v.Identifier.Text;
                                        me.Uses.Add(new UseRefDto { Target = qn, Via = "field", Member = memberName });
                                        edges.Add(new EdgeDto { From = meId, To = qn, Rel = "field" });
                                    }
                                }
                                else externalRefs.Add(qn);
                            }
                        }
                        else if (member is PropertyDeclarationSyntax p)
                        {
                            var typeSym = model.GetTypeInfo(p.Type).Type;
                            foreach (var u in CandidateUserTypeSymbols(typeSym))
                            {
                                if (u is null) continue;
                                var qn = GetQualifiedName(u);
                                if (IsUserType(u))
                                {
                                    me.Uses.Add(new UseRefDto { Target = qn, Via = "property", Member = p.Identifier.Text });
                                    edges.Add(new EdgeDto { From = meId, To = qn, Rel = "property" });
                                }
                                else externalRefs.Add(qn);
                            }
                        }
                    }

                    if (includeMethods)
                    {
                        foreach (var member in GetMembers(node))
                        {
                            if (member is MethodDeclarationSyntax m)
                            {
                                var msym = model.GetDeclaredSymbol(m) as IMethodSymbol;
                                if (msym is null) continue;

                                if (!msym.ReturnsVoid && msym.ReturnType is not null)
                                {
                                    foreach (var u in CandidateUserTypeSymbols(msym.ReturnType))
                                    {
                                        var qn = GetQualifiedName(u);
                                        if (IsUserType(u))
                                        {
                                            me.Uses.Add(new UseRefDto { Target = qn, Via = "return", Member = m.Identifier.Text });
                                            edges.Add(new EdgeDto { From = meId, To = qn, Rel = "return" });
                                        }
                                        else externalRefs.Add(qn);
                                    }
                                }
                                foreach (var p in msym.Parameters)
                                {
                                    foreach (var u in CandidateUserTypeSymbols(p.Type))
                                    {
                                        var qn = GetQualifiedName(u);
                                        if (IsUserType(u))
                                        {
                                            me.Uses.Add(new UseRefDto { Target = qn, Via = "param", Member = p.Name });
                                            edges.Add(new EdgeDto { From = meId, To = qn, Rel = "param" });
                                        }
                                        else externalRefs.Add(qn);
                                    }
                                }
                            }
                            else if (member is ConstructorDeclarationSyntax ctor)
                            {
                                var csym = model.GetDeclaredSymbol(ctor) as IMethodSymbol;
                                if (csym is null) continue;

                                foreach (var p in csym.Parameters)
                                {
                                    foreach (var u in CandidateUserTypeSymbols(p.Type))
                                    {
                                        var qn = GetQualifiedName(u);
                                        if (IsUserType(u))
                                        {
                                            me.Uses.Add(new UseRefDto { Target = qn, Via = "param", Member = p.Name });
                                            edges.Add(new EdgeDto { From = meId, To = qn, Rel = "param" });
                                        }
                                        else externalRefs.Add(qn);
                                    }
                                }
                            }
                        }
                    }
                }

                foreach (var e in edges)
                {
                    if (typesById.TryGetValue(e.To, out var target))
                        if (!target.UsedBy.Contains(e.From)) target.UsedBy.Add(e.From);
                }

                foreach (var t in typesById.Values)
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

                var orderedTypes = typesById.Values
                    .OrderBy(t => t.Namespace, StringComparer.Ordinal)
                    .ThenBy(t => t.Name, StringComparer.Ordinal)
                    .ToList();

                var orderedEdges = edges
                    .OrderBy(e => e.From, StringComparer.Ordinal)
                    .ThenBy(e => e.To, StringComparer.Ordinal)
                    .ThenBy(e => e.Rel, StringComparer.Ordinal)
                    .ToList();

                return new GraphDto
                {
                    Types = orderedTypes,
                    Edges = orderedEdges,
                    ExternalRefCount = externalRefs.Count  
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
            var parts = new List<string>();
            for (var cur = s; cur is not null; cur = cur.ContainingType)
                parts.Insert(0, cur.Name);

            var ns = s.ContainingNamespace?.ToDisplayString();
            return string.IsNullOrEmpty(ns)
                ? string.Join(".", parts)
                : $"{ns}.{string.Join(".", parts)}";
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