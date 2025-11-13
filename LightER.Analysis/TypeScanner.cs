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
    public static class TypeScanner
    {
        private static IEnumerable<MemberDeclarationSyntax> GetMembers(BaseTypeDeclarationSyntax node)
        {
            return node switch
            {
                ClassDeclarationSyntax c => c.Members,
                StructDeclarationSyntax s => s.Members,
                InterfaceDeclarationSyntax i => i.Members,
                RecordDeclarationSyntax r => r.Members,
                EnumDeclarationSyntax _ => Array.Empty<MemberDeclarationSyntax>(),
                _ => Array.Empty<MemberDeclarationSyntax>()
            };
        }

        public static IReadOnlyList<TypeInfoDto> ScanTypes(IEnumerable<string> csFilePaths)
        {
            var results = new List<TypeInfoDto>();

            foreach (var path in csFilePaths)
            {
                var text = File.ReadAllText(path);
                var tree = CSharpSyntaxTree.ParseText(text);
                var root = tree.GetRoot();

                foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (node.Parent is BaseTypeDeclarationSyntax)
                        continue;

                    var name = GetIdentifier(node);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var ns = GetNamespace(node) ?? string.Empty;
                    var id = string.IsNullOrEmpty(ns) ? name! : $"{ns}.{name}";

                    results.Add(new TypeInfoDto
                    {
                        Id = id,
                        Name = name!,
                        Namespace = ns,
                        Kind = GetKind(node),
                        File = Path.GetFileName(path)
                    });
                }
            }

            return results
                .OrderBy(t => t.Namespace, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        }

        public static GraphDto ScanGraph(IEnumerable<string> csFilePaths)
        {
            var types = new List<TypeInfoDto>();
            var typeDecls = new List<(BaseTypeDeclarationSyntax Node, string File)>();

            foreach (var path in csFilePaths)
            {
                var text = File.ReadAllText(path);
                var tree = CSharpSyntaxTree.ParseText(text);
                var root = tree.GetRoot();

                foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (node.Parent is BaseTypeDeclarationSyntax) continue;

                    var name = GetIdentifier(node);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var ns = GetNamespace(node) ?? string.Empty;
                    var id = string.IsNullOrEmpty(ns) ? name! : $"{ns}.{name}";

                    types.Add(new TypeInfoDto
                    {
                        Id = id,
                        Name = name!,
                        Namespace = ns,
                        Kind = GetKind(node),
                        File = Path.GetFileName(path),
                        Inherits = new List<string>(),
                        Implements = new List<string>(),
                        Uses = new List<UseRefDto>(),
                        UsedBy = new List<string>()
                    });

                    typeDecls.Add((node, path));
                }
            }

            var byId = new Dictionary<string, TypeInfoDto>(StringComparer.Ordinal);
            foreach (var t in types) byId.TryAdd(t.Id, t);

            var byName = types.GroupBy(t => t.Name)
                              .ToDictionary(g => g.Key, g => g.Select(t => t).ToList(), StringComparer.Ordinal);

            var rawEdges = new HashSet<EdgeDto>(new EdgeComparer());

            for (int i = 0; i < typeDecls.Count; i++)
            {
                var (node, filePath) = typeDecls[i];
                var me = types[i]; 

                switch (node)
                {
                    case ClassDeclarationSyntax c when c.BaseList is not null:
                    case RecordDeclarationSyntax r when r.BaseList is not null:
                        {
                            var baseTypes = GetBaseTypeNames(node);
                            if (baseTypes.Count > 0)
                            {
                                var first = ResolveToIdOrKeep(baseTypes[0], byId, byName);
                                me.Inherits.Add(first);
                                rawEdges.Add(new EdgeDto { From = me.Id, To = first, Rel = "inherits" });

                                for (int k = 1; k < baseTypes.Count; k++)
                                {
                                    var iface = ResolveToIdOrKeep(baseTypes[k], byId, byName);
                                    me.Implements.Add(iface);
                                    rawEdges.Add(new EdgeDto { From = me.Id, To = iface, Rel = "implements" });
                                }
                            }
                            break;
                        }
                    case StructDeclarationSyntax s when s.BaseList is not null:
                        {
                            foreach (var n in GetBaseTypeNames(node))
                            {
                                var iface = ResolveToIdOrKeep(n, byId, byName);
                                me.Implements.Add(iface);
                                rawEdges.Add(new EdgeDto { From = me.Id, To = iface, Rel = "implements" });
                            }
                            break;
                        }
                    case InterfaceDeclarationSyntax itf when itf.BaseList is not null:
                        {
                            foreach (var n in GetBaseTypeNames(node))
                            {
                                var parent = ResolveToIdOrKeep(n, byId, byName);
                                me.Inherits.Add(parent);
                                rawEdges.Add(new EdgeDto { From = me.Id, To = parent, Rel = "inherits" });
                            }
                            break;
                        }
                }

                foreach (var member in GetMembers(node))
                {
                    if (member is FieldDeclarationSyntax f)
                    {
                        var candidates = GetCandidateTypeNames(f.Declaration.Type);
                        foreach (var cand in FilterToUserTypes(candidates, byId, byName))
                        {
                            foreach (var v in f.Declaration.Variables)
                            {
                                var memberName = v.Identifier.Text;
                                var target = ResolveToIdOrKeep(cand, byId, byName);
                                me.Uses.Add(new UseRefDto { Target = target, Via = "field", Member = memberName });
                                rawEdges.Add(new EdgeDto { From = me.Id, To = target, Rel = "field" });
                            }
                        }
                    }
                    else if (member is PropertyDeclarationSyntax p)
                    {
                        var candidates = GetCandidateTypeNames(p.Type);
                        foreach (var cand in FilterToUserTypes(candidates, byId, byName))
                        {
                            var target = ResolveToIdOrKeep(cand, byId, byName);
                            me.Uses.Add(new UseRefDto { Target = target, Via = "property", Member = p.Identifier.Text });
                            rawEdges.Add(new EdgeDto { From = me.Id, To = target, Rel = "property" });
                        }
                    }
                }

            }

            foreach (var e in rawEdges)
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

            var edges = rawEdges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Rel, StringComparer.Ordinal)
                .ToList();

            var merged = new Dictionary<string, TypeInfoDto>(StringComparer.Ordinal);

            foreach (var t in types)
            {
                if (!merged.TryGetValue(t.Id, out var agg))
                {
                    agg = new TypeInfoDto
                    {
                        Id = t.Id,
                        Name = t.Name,
                        Namespace = t.Namespace,
                        Kind = t.Kind,
                        File = t.File,
                        Inherits = new List<string>(),
                        Implements = new List<string>(),
                        Uses = new List<UseRefDto>(),
                        UsedBy = new List<string>()
                    };
                    merged[t.Id] = agg;
                }

                agg.Inherits.AddRange(t.Inherits);
                agg.Implements.AddRange(t.Implements);
                agg.Uses.AddRange(t.Uses);
                agg.UsedBy.AddRange(t.UsedBy);
            }

            foreach (var m in merged.Values)
            {
                m.Inherits = m.Inherits.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                m.Implements = m.Implements.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                m.Uses = m.Uses
                    .OrderBy(u => u.Target, StringComparer.Ordinal)
                    .ThenBy(u => u.Via, StringComparer.Ordinal)
                    .ThenBy(u => u.Member, StringComparer.Ordinal)
                    .ToList();
                m.UsedBy = m.UsedBy.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            }

            var typesOut = merged.Values
                .OrderBy(t => t.Namespace, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();


            return new GraphDto
            {
                Types = typesOut,
                Edges = edges
            };
        }

        private static string GetKind(BaseTypeDeclarationSyntax node) =>
            node.Kind() switch
            {
                SyntaxKind.ClassDeclaration => "class",
                SyntaxKind.StructDeclaration => "struct",
                SyntaxKind.InterfaceDeclaration => "interface",
                SyntaxKind.EnumDeclaration => "enum",
                SyntaxKind.RecordDeclaration => "record",
                _ => "type"
            };

        private static string? GetIdentifier(BaseTypeDeclarationSyntax node) => node switch
        {
            ClassDeclarationSyntax x => x.Identifier.Text,
            StructDeclarationSyntax x => x.Identifier.Text,
            InterfaceDeclarationSyntax x => x.Identifier.Text,
            EnumDeclarationSyntax x => x.Identifier.Text,
            RecordDeclarationSyntax x => x.Identifier.Text,
            _ => null
        };

        private static string? GetNamespace(SyntaxNode node)
        {
            for (SyntaxNode? cur = node.Parent; cur is not null; cur = cur.Parent)
            {
                switch (cur)
                {
                    case FileScopedNamespaceDeclarationSyntax f:
                        return NameToString(f.Name);
                    case NamespaceDeclarationSyntax n:
                        return NameToString(n.Name);
                }
            }
            return null;
        }

        private static string NameToString(NameSyntax name) => name switch
        {
            IdentifierNameSyntax i => i.Identifier.Text,
            QualifiedNameSyntax q => $"{NameToString(q.Left)}.{NameToString(q.Right)}",
            GenericNameSyntax g => g.Identifier.Text, 
            AliasQualifiedNameSyntax a => NameToString(a.Name),
            _ => name.ToString()
        };

        private static List<string> GetBaseTypeNames(BaseTypeDeclarationSyntax node)
        {
            BaseListSyntax? baseList = node switch
            {
                ClassDeclarationSyntax c => c.BaseList,
                StructDeclarationSyntax s => s.BaseList,
                InterfaceDeclarationSyntax i => i.BaseList,
                RecordDeclarationSyntax r => r.BaseList,
                _ => null
            };
            var result = new List<string>();
            if (baseList is null) return result;

            foreach (var t in baseList.Types)
            {
                if (t.Type is NameSyntax ns)
                    result.Add(NameToString(ns));
                else
                    result.Add(t.Type.ToString()); 
            }
            return result;
        }

        private static IEnumerable<string> GetCandidateTypeNames(TypeSyntax type)
        {
            switch (type)
            {
                case NullableTypeSyntax n:
                    return GetCandidateTypeNames(n.ElementType);
                case ArrayTypeSyntax a:
                    return GetCandidateTypeNames(a.ElementType);
                case PointerTypeSyntax p:
                    return GetCandidateTypeNames(p.ElementType);
                case TupleTypeSyntax _:
                    return Array.Empty<string>();
                case GenericNameSyntax g:
                    return g.TypeArgumentList.Arguments.SelectMany(GetCandidateTypeNames);
                case NameSyntax ns:
                    return new[] { NameToString(ns) };
                case PredefinedTypeSyntax _:
                    return Array.Empty<string>();
                default:
                    return new[] { type.ToString() };
            }
        }

        private static IEnumerable<string> FilterToUserTypes(IEnumerable<string> candidates,
                                                             IDictionary<string, TypeInfoDto> byId,
                                                             IDictionary<string, List<TypeInfoDto>> byName)
        {
            foreach (var c in candidates)
            {
                if (byId.ContainsKey(c))
                {
                    yield return c;
                    continue;
                }

                var simple = c.Contains('.') ? c.Split('.').Last() : c;
                if (byName.TryGetValue(simple, out var matches) && matches.Count > 0)
                    yield return c;
            }
        }

        private static string ResolveToIdOrKeep(string name,
                                                IDictionary<string, TypeInfoDto> byId,
                                                IDictionary<string, List<TypeInfoDto>> byName)
        {
            if (byId.ContainsKey(name)) return name;

            var simple = name.Contains('.') ? name.Split('.').Last() : name;

            if (byName.TryGetValue(simple, out var matches))
            {
                if (matches.Count == 1) return matches[0].Id;

                return simple;
            }

            return simple;
        }
    }


}


