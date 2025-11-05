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

                    var ns = GetNameSpace(node) ?? string.Empty;
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

              return results.OrderBy(t => t.Namespace, StringComparer.Ordinal)
                              .ThenBy(t => t.Name, StringComparer.Ordinal)
                              .ToList();
        }

        private static string GetKind(BaseTypeDeclarationSyntax node) => node.Kind() switch
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

        private static string? GetNameSpace(SyntaxNode node)
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
    }
}


