﻿// Copyright (c) .NET Foundation and Contributors. Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslynator.CodeFixes;
using Roslynator.CSharp.CodeStyle;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Roslynator.CSharp.CSharpFactory;

namespace Roslynator.CSharp.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EnumMemberDeclarationCodeFixProvider))]
[Shared]
public sealed class EnumMemberDeclarationCodeFixProvider : BaseCodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds
    {
        get
        {
            return ImmutableArray.Create(
                DiagnosticIdentifiers.DeclareEnumValueAsCombinationOfNames,
                DiagnosticIdentifiers.DuplicateEnumValue,
                DiagnosticIdentifiers.NormalizeFormatOfEnumFlagValue);
        }
    }

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode root = await context.GetSyntaxRootAsync().ConfigureAwait(false);

        if (!TryFindFirstAncestorOrSelf(root, context.Span, out EnumMemberDeclarationSyntax enumMemberDeclaration))
            return;

        Document document = context.Document;
        Diagnostic diagnostic = context.Diagnostics[0];

        switch (diagnostic.Id)
        {
            case DiagnosticIdentifiers.DeclareEnumValueAsCombinationOfNames:
            {
                CodeAction codeAction = CodeAction.Create(
                    "Declare value as combination of names",
                    ct => DeclareEnumValueAsCombinationOfNamesAsync(document, enumMemberDeclaration, ct),
                    GetEquivalenceKey(diagnostic));

                context.RegisterCodeFix(codeAction, diagnostic);
                break;
            }
            case DiagnosticIdentifiers.DuplicateEnumValue:
            {
                SemanticModel semanticModel = await context.GetSemanticModelAsync().ConfigureAwait(false);

                var enumDeclaration = (EnumDeclarationSyntax)enumMemberDeclaration.Parent;

                IFieldSymbol fieldSymbol = semanticModel.GetDeclaredSymbol(enumMemberDeclaration, context.CancellationToken);

                EnumFieldSymbolInfo enumFieldSymbolInfo = EnumFieldSymbolInfo.Create(fieldSymbol);

                string valueText = FindMemberByValue(enumDeclaration, enumFieldSymbolInfo, semanticModel, context.CancellationToken).Identifier.ValueText;

                CodeAction codeAction = CodeAction.Create(
                    $"Change enum value to '{valueText}'",
                    ct => ChangeEnumValueAsync(document, enumMemberDeclaration, valueText, ct),
                    GetEquivalenceKey(diagnostic));

                context.RegisterCodeFix(codeAction, diagnostic);
                break;
            }
            case DiagnosticIdentifiers.NormalizeFormatOfEnumFlagValue:
            {
                EnumFlagValueStyle style = document.GetConfigOptions(enumMemberDeclaration.SyntaxTree).GetEnumFlagValueStyle();

                if (style == EnumFlagValueStyle.ShiftOperator)
                {
                    CodeAction codeAction = CodeAction.Create(
                        "Use '<<' operator",
                        ct => UseBitShiftOperatorAsync(document, enumMemberDeclaration, ct),
                        GetEquivalenceKey(diagnostic));

                    context.RegisterCodeFix(codeAction, diagnostic);
                }
                else if (style == EnumFlagValueStyle.DecimalNumber)
                {
                    CodeAction codeAction = CodeAction.Create(
                        "Convert to decimal",
                        ct => ConvertToDecimalNumberAsync(document, enumMemberDeclaration, ct),
                        GetEquivalenceKey(diagnostic));

                    context.RegisterCodeFix(codeAction, diagnostic);
                }
                else
                {
                    throw new InvalidOperationException();
                }

                break;
            }
        }
    }

    private static async Task<Document> DeclareEnumValueAsCombinationOfNamesAsync(
        Document document,
        EnumMemberDeclarationSyntax enumMemberDeclaration,
        CancellationToken cancellationToken)
    {
        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        IFieldSymbol fieldSymbol = semanticModel.GetDeclaredSymbol(enumMemberDeclaration, cancellationToken);

        EnumSymbolInfo enumInfo = EnumSymbolInfo.Create(fieldSymbol.ContainingType);

        EnumFieldSymbolInfo fieldInfo = EnumFieldSymbolInfo.Create(fieldSymbol);

        List<EnumFieldSymbolInfo> values = enumInfo.Decompose(fieldInfo);

        values.Sort((f, g) =>
        {
            if (f.HasCompositeValue())
            {
                if (g.HasCompositeValue())
                {
                    return f.Value.CompareTo(g.Value);
                }
                else
                {
                    return -1;
                }
            }
            else if (g.HasCompositeValue())
            {
                return 1;
            }

            return f.Value.CompareTo(g.Value);
        });

        ExpressionSyntax oldValue = enumMemberDeclaration.EqualsValue.Value.WalkDownParentheses();

        BinaryExpressionSyntax newValue = BitwiseOrExpression(CreateIdentifierName(values[0]), CreateIdentifierName(values[1]));

        for (int i = 2; i < values.Count; i++)
            newValue = BitwiseOrExpression(newValue, CreateIdentifierName(values[i]));

        newValue = newValue.WithFormatterAnnotation();

        return await document.ReplaceNodeAsync(oldValue, newValue, cancellationToken).ConfigureAwait(false);
    }

    private static IdentifierNameSyntax CreateIdentifierName(in EnumFieldSymbolInfo fieldInfo)
    {
        return IdentifierName(fieldInfo.Name);
    }

    private static Task<Document> ChangeEnumValueAsync(
        Document document,
        EnumMemberDeclarationSyntax enumMember,
        string valueText,
        CancellationToken cancellationToken)
    {
        EqualsValueClauseSyntax equalsValue = enumMember.EqualsValue;

        EnumMemberDeclarationSyntax newEnumMember;

        if (equalsValue is not null)
        {
            IdentifierNameSyntax newValue = IdentifierName(Identifier(equalsValue.Value.GetLeadingTrivia(), valueText, equalsValue.Value.GetTrailingTrivia()));
            newEnumMember = enumMember.WithEqualsValue(equalsValue.WithValue(newValue));
        }
        else
        {
            IdentifierNameSyntax newValue = IdentifierName(Identifier(valueText));
            newEnumMember = enumMember.WithEqualsValue(EqualsValueClause(newValue));
        }

        return document.ReplaceNodeAsync(enumMember, newEnumMember, cancellationToken);
    }

    private static EnumMemberDeclarationSyntax FindMemberByValue(
        EnumDeclarationSyntax enumDeclaration,
        in EnumFieldSymbolInfo fieldSymbolInfo,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (EnumMemberDeclarationSyntax enumMember in enumDeclaration.Members)
        {
            IFieldSymbol fieldSymbol = semanticModel.GetDeclaredSymbol(enumMember, cancellationToken);

            if (CSharpUtility.IsSymbolObsolete(fieldSymbol))
                continue;

            if (!SymbolEqualityComparer.Default.Equals(fieldSymbolInfo.Symbol, fieldSymbol))
            {
                EnumFieldSymbolInfo fieldSymbolInfo2 = EnumFieldSymbolInfo.Create(fieldSymbol);

                if (fieldSymbolInfo2.HasValue
                    && fieldSymbolInfo.Value == fieldSymbolInfo2.Value)
                {
                    return enumMember;
                }
            }
        }

        throw new InvalidOperationException();
    }

    private static async Task<Document> UseBitShiftOperatorAsync(
        Document document,
        EnumMemberDeclarationSyntax enumMemberDeclaration,
        CancellationToken cancellationToken)
    {
        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        ExpressionSyntax expression = enumMemberDeclaration.EqualsValue?.Value.WalkDownParentheses();

        Optional<object> constantValue = semanticModel.GetConstantValue(expression, cancellationToken);

        var power = (int)Math.Log(Convert.ToDouble(constantValue.Value), 2);

        BinaryExpressionSyntax leftShift = LeftShiftExpression(NumericLiteralExpression(1), NumericLiteralExpression(power));

        BinaryExpressionSyntax newExpression = leftShift.WithTriviaFrom(expression);

        return await document.ReplaceNodeAsync(expression, newExpression, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Document> ConvertToDecimalNumberAsync(
        Document document,
        EnumMemberDeclarationSyntax enumMemberDeclaration,
        CancellationToken cancellationToken)
    {
        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        IFieldSymbol fieldSymbol = semanticModel.GetDeclaredSymbol(enumMemberDeclaration, cancellationToken);

        ExpressionSyntax expression = enumMemberDeclaration.EqualsValue?.Value.WalkDownParentheses();

        string text = EnumFieldSymbolInfo.Create(fieldSymbol).Value.ToString("d");

        ExpressionSyntax newExpression = ParseExpression(text).WithTriviaFrom(expression);

        return await document.ReplaceNodeAsync(expression, newExpression, cancellationToken).ConfigureAwait(false);
    }
}
