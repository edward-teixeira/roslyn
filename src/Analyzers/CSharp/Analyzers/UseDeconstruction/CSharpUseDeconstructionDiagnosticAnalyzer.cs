﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.UseDeconstruction
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    internal class CSharpUseDeconstructionDiagnosticAnalyzer : AbstractBuiltInCodeStyleDiagnosticAnalyzer
    {
        public CSharpUseDeconstructionDiagnosticAnalyzer()
            : base(IDEDiagnosticIds.UseDeconstructionDiagnosticId,
                   EnforceOnBuildValues.UseDeconstruction,
                   CSharpCodeStyleOptions.PreferDeconstructedVariableDeclaration,
                   new LocalizableResourceString(nameof(CSharpAnalyzersResources.Deconstruct_variable_declaration), CSharpAnalyzersResources.ResourceManager, typeof(CSharpAnalyzersResources)),
                   new LocalizableResourceString(nameof(CSharpAnalyzersResources.Variable_declaration_can_be_deconstructed), CSharpAnalyzersResources.ResourceManager, typeof(CSharpAnalyzersResources)))
        {
        }

        public override DiagnosticAnalyzerCategory GetAnalyzerCategory()
            => DiagnosticAnalyzerCategory.SemanticSpanAnalysis;

        protected override void InitializeWorker(AnalysisContext context)
        {
            context.RegisterSyntaxNodeAction(AnalyzeNode,
                SyntaxKind.VariableDeclaration, SyntaxKind.ForEachStatement);
        }

        private void AnalyzeNode(SyntaxNodeAnalysisContext context)
        {
            var option = context.GetCSharpAnalyzerOptions().PreferDeconstructedVariableDeclaration;
            if (!option.Value)
                return;

            switch (context.Node)
            {
                case VariableDeclarationSyntax variableDeclaration:
                    AnalyzeVariableDeclaration(context, variableDeclaration, option.Notification.Severity);
                    return;
                case ForEachStatementSyntax forEachStatement:
                    AnalyzeForEachStatement(context, forEachStatement, option.Notification.Severity);
                    return;
            }
        }

        private void AnalyzeVariableDeclaration(
            SyntaxNodeAnalysisContext context, VariableDeclarationSyntax variableDeclaration, ReportDiagnostic severity)
        {
            if (!TryAnalyzeVariableDeclaration(context.SemanticModel, variableDeclaration, out _, out _, context.CancellationToken))
                return;

            context.ReportDiagnostic(DiagnosticHelper.Create(
                Descriptor,
                variableDeclaration.Variables[0].Identifier.GetLocation(),
                severity,
                additionalLocations: null,
                properties: null));
        }

        private void AnalyzeForEachStatement(
            SyntaxNodeAnalysisContext context, ForEachStatementSyntax forEachStatement, ReportDiagnostic severity)
        {
            if (!TryAnalyzeForEachStatement(context.SemanticModel, forEachStatement, out _, out _, context.CancellationToken))
                return;

            context.ReportDiagnostic(DiagnosticHelper.Create(
                Descriptor,
                forEachStatement.Identifier.GetLocation(),
                severity,
                additionalLocations: null,
                properties: null));
        }

        public static bool TryAnalyzeVariableDeclaration(
            SemanticModel semanticModel,
            VariableDeclarationSyntax variableDeclaration,
            [NotNullWhen(true)] out INamedTypeSymbol? tupleType,
            out ImmutableArray<MemberAccessExpressionSyntax> memberAccessExpressions,
            CancellationToken cancellationToken)
        {
            tupleType = null;
            memberAccessExpressions = default;

            // Only support code of the form:
            //
            //      var t = ...;  or
            //      (T1 e1, ..., TN eN) t = ...
            if (variableDeclaration is not { Parent: LocalDeclarationStatementSyntax localDeclaration, Variables: [{ Initializer.Value: { } initializerValue } declarator] })
                return false;

            var local = (ILocalSymbol)semanticModel.GetRequiredDeclaredSymbol(declarator, cancellationToken);

            var initializerConversion = semanticModel.GetConversion(initializerValue, cancellationToken);

            var searchScope = localDeclaration.Parent is GlobalStatementSyntax globalStatement
                ? globalStatement.GetRequiredParent()
                : localDeclaration.GetRequiredParent();

            return TryAnalyze(
                semanticModel, local, variableDeclaration.Type, declarator.Identifier, initializerConversion, searchScope,
                out tupleType, out memberAccessExpressions, cancellationToken);
        }

        public static bool TryAnalyzeForEachStatement(
            SemanticModel semanticModel,
            ForEachStatementSyntax forEachStatement,
            [NotNullWhen(true)] out INamedTypeSymbol? tupleType,
            out ImmutableArray<MemberAccessExpressionSyntax> memberAccessExpressions,
            CancellationToken cancellationToken)
        {
            var local = (ILocalSymbol)semanticModel.GetRequiredDeclaredSymbol(forEachStatement, cancellationToken);
            var elementConversion = semanticModel.GetForEachStatementInfo(forEachStatement).ElementConversion;

            return TryAnalyze(
                semanticModel, local, forEachStatement.Type, forEachStatement.Identifier, elementConversion,
                forEachStatement, out tupleType, out memberAccessExpressions, cancellationToken);
        }

        private static bool TryAnalyze(
            SemanticModel semanticModel,
            ILocalSymbol local,
            TypeSyntax typeNode,
            SyntaxToken identifier,
            Conversion conversion,
            SyntaxNode searchScope,
            [NotNullWhen(true)] out INamedTypeSymbol? tupleType,
            out ImmutableArray<MemberAccessExpressionSyntax> memberAccessExpressions,
            CancellationToken cancellationToken)
        {
            tupleType = null;
            memberAccessExpressions = default;

            if (identifier.IsMissing)
                return false;

            if (!IsViableTupleTypeSyntax(typeNode))
                return false;

            if (conversion.Exists &&
                !conversion.IsIdentity &&
                !conversion.IsTupleConversion &&
                !conversion.IsTupleLiteralConversion)
            {
                // If there is any other conversion, we bail out because the source type might not be a tuple
                // or it is a tuple but only thanks to target type inference, which won't occur in a deconstruction.
                // Interesting case that illustrates this is initialization with a default literal:
                // (int a, int b) t = default;
                // This is classified as conversion.IsNullLiteral.
                return false;
            }

            var type = semanticModel.GetTypeInfo(typeNode, cancellationToken).Type;
            if (type is not INamedTypeSymbol { IsTupleType: true, TupleElements.Length: >= 2 } tupleTypeOpt)
                return false;

            tupleType = tupleTypeOpt;
            // All tuple elements must have been explicitly provided by the user.
            foreach (var element in tupleType.TupleElements)
            {
                if (element.IsImplicitlyDeclared)
                    return false;
            }

            using var _ = ArrayBuilder<MemberAccessExpressionSyntax>.GetInstance(out var references);

            // If the user actually uses the tuple local for anything other than accessing 
            // fields off of it, then we can't deconstruct this tuple into locals.
            if (!OnlyUsedToAccessTupleFields(
                    semanticModel, searchScope, local, references, cancellationToken))
            {
                return false;
            }

            // Can only deconstruct the tuple if the names we introduce won't collide
            // with anything else in scope (either outside, or inside the method).
            if (AnyTupleFieldNamesCollideWithExistingNames(
                    semanticModel, tupleType, searchScope, cancellationToken))
            {
                return false;
            }

            memberAccessExpressions = references.ToImmutableAndClear();
            return true;
        }

        private static bool AnyTupleFieldNamesCollideWithExistingNames(
            SemanticModel semanticModel, INamedTypeSymbol tupleType,
            SyntaxNode container, CancellationToken cancellationToken)
        {
            var existingSymbols = GetExistingSymbols(semanticModel, container, cancellationToken);

            var reservedNames = semanticModel.LookupSymbols(container.SpanStart)
                                             .Select(s => s.Name)
                                             .Concat(existingSymbols.Select(s => s.Name))
                                             .ToSet();

            foreach (var element in tupleType.TupleElements)
            {
                if (reservedNames.Contains(element.Name))
                    return true;
            }

            return false;
        }

        private static bool IsViableTupleTypeSyntax(TypeSyntax type)
        {
            if (type.IsVar)
            {
                // 'var t' can be converted to 'var (x, y, z)'
                return true;
            }

            if (type is TupleTypeSyntax tupleType)
            {
                // '(int x, int y) t' can be converted to '(int x, int y)'.  So all the elements
                // need names.

                foreach (var element in tupleType.Elements)
                {
                    if (element.Identifier.IsKind(SyntaxKind.None))
                        return false;
                }

                return true;
            }

            return false;
        }

        private static bool OnlyUsedToAccessTupleFields(
            SemanticModel semanticModel, SyntaxNode searchScope, ILocalSymbol local,
            ArrayBuilder<MemberAccessExpressionSyntax> memberAccessLocations, CancellationToken cancellationToken)
        {
            var localName = local.Name;

            foreach (var identifierName in searchScope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifierName.Identifier.ValueText == localName)
                {
                    var symbol = semanticModel.GetSymbolInfo(identifierName, cancellationToken).GetAnySymbol();
                    if (local.Equals(symbol))
                    {
                        if (identifierName.Parent is not MemberAccessExpressionSyntax memberAccess)
                        {
                            // We referenced the local in a location where we're not accessing a 
                            // field off of it.  i.e. Console.WriteLine(tupleLocal);
                            return false;
                        }

                        var member = semanticModel.GetSymbolInfo(memberAccess, cancellationToken).GetAnySymbol();
                        if (member is not IFieldSymbol field)
                        {
                            // Accessed some non-field member of it (like .ToString()).
                            return false;
                        }

                        if (field.IsImplicitlyDeclared)
                        {
                            // They're referring to .Item1-.ItemN.  We can't update this to refer to the local
                            return false;
                        }

                        memberAccessLocations.Add(memberAccess);
                    }
                }
            }

            return true;
        }

        private static IEnumerable<ISymbol> GetExistingSymbols(
            SemanticModel semanticModel, SyntaxNode container, CancellationToken cancellationToken)
        {
            // Ignore an anonymous type property.  It's ok if they have a name that 
            // matches the name of the local we're introducing.
            return semanticModel.GetAllDeclaredSymbols(container, cancellationToken)
                                .Where(s => !s.IsAnonymousTypeProperty() && !s.IsTupleField());
        }
    }
}
