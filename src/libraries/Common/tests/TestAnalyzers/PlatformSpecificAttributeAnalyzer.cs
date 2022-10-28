// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.Interop.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class PlatformSpecificAttributeAnalyzer : DiagnosticAnalyzer
    {
        private const string Category = "Usage";
        private const string Title = "No negation in PlatformSpecific attribute";
        public const string MessageFormat = "Attribute '{0}' is wrong";
        private const string Description = "Remove negation of enum values, e.g. ~TestPlatforms.tvOS, in PlatformSpecific attributes. Replace them with SkipOnPlatform instead.";

        internal static DiagnosticDescriptor Rule =
            new DiagnosticDescriptor(
                "DOTNETRUNTIME0001",
                Title,
                MessageFormat,
                Category,
                DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            // Don't analyze generated code
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(PrepareForAnalysis);
        }

        private void PrepareForAnalysis(CompilationStartAnalysisContext context)
        {
            var perCompilationAnalyzer = new PerCompilationAnalyzer(context.Compilation);

            // TODO: Change this from a SyntaxNode action to an operation attribute once attribute application is represented in the
            // IOperation tree by Roslyn.
            context.RegisterSyntaxNodeAction(perCompilationAnalyzer.AnalyzeAttribute, SyntaxKind.Attribute);
        }

        private sealed partial class PerCompilationAnalyzer
        {
            private readonly Compilation _compilation;

            public PerCompilationAnalyzer(Compilation compilation)
            {
                _compilation = compilation;
            }

            public void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
            {
                AttributeSyntax syntax = (AttributeSyntax)context.Node;
                ISymbol attributedSymbol = context.ContainingSymbol!;

                AttributeData? attr = FindAttributeData(syntax, attributedSymbol);

                if (attr?.AttributeClass?.ToDisplayString() == "Xunit.PlatformSpecificAttribute" && syntax.ArgumentList is not null)
                {
                context.ReportDiagnostic(
                        Diagnostic.Create(
                            Rule,
                            syntax.GetLocation(),
                            (object)"foo"));

                   // INamedTypeSymbol? platformValue = (INamedTypeSymbol?)syntax.ArgumentList[0].Value;

                   // Diagnostic diagnostic = Diagnostic.Create(Rule, platformValue.Locations[0], platformValue.Name);
                   // context.ReportDiagnostic(diagnostic);
                }
            }

            static AttributeData? FindAttributeData(AttributeSyntax syntax, ISymbol targetSymbol)
            {
                AttributeTargetSpecifierSyntax attributeTarget = syntax.FirstAncestorOrSelf<AttributeListSyntax>().Target;
                if (attributeTarget is not null)
                {
                    switch (attributeTarget.Identifier.Kind())
                    {
                        case SyntaxKind.ReturnKeyword:
                            if (targetSymbol is IMethodSymbol method)
                            {
                                // Sometimes an attribute is put on a symbol that is nested within the containing symbol.
                                // For example, the ContainingSymbol for an AttributeSyntax on a local function has a ContainingSymbol of the method.
                                // Since this method is internal and the callers don't care about attributes on local functions,
                                // we just allow this method to return null in those cases.
                                return method.GetReturnTypeAttributes().FirstOrDefault(attributeSyntaxLocationMatches);
                            }
                            // An attribute on the return value of a delegate type's Invoke method has a ContainingSymbol of the delegate type.
                            // We don't care about the attributes in this case for the callers, so we'll just return null.
                            return null;
                        case SyntaxKind.AssemblyKeyword:
                            return targetSymbol.ContainingAssembly.GetAttributes().First(attributeSyntaxLocationMatches);
                        case SyntaxKind.ModuleKeyword:
                            return targetSymbol.ContainingModule.GetAttributes().First(attributeSyntaxLocationMatches);
                        default:
                            return null;
                    }
                }
                // Sometimes an attribute is put on a symbol that is nested within the containing symbol.
                // For example, the ContainingSymbol for an AttributeSyntax on a parameter has a ContainingSymbol of the method
                // and an AttributeSyntax on a local function has a ContainingSymbol of the containing method.
                // Since this method is internal and the callers don't care about attributes on parameters, we just allow
                // this method to return null in those cases.
                return targetSymbol.GetAttributes().FirstOrDefault(attributeSyntaxLocationMatches);

                bool attributeSyntaxLocationMatches(AttributeData attrData)
                {
                    return attrData.ApplicationSyntaxReference!.SyntaxTree == syntax.SyntaxTree && attrData.ApplicationSyntaxReference.Span == syntax.Span;
                }
            }
        }
    }
}
