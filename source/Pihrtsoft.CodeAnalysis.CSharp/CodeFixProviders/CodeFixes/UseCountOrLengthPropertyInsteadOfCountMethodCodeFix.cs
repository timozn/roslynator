﻿// Copyright (c) Josef Pihrt. All rights reserved. Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Pihrtsoft.CodeAnalysis.CSharp.CodeFixProviders.CodeFixes
{
    internal static class UseCountOrLengthPropertyInsteadOfCountMethodCodeFix
    {
        public static void Register(
            CodeFixContext context,
            Diagnostic diagnostic,
            InvocationExpressionSyntax invocation)
        {
            string propertyName = diagnostic.Properties["PropertyName"];

            CodeAction codeAction = CodeAction.Create(
                $"Replace 'Count()' with '{propertyName}'",
                cancellationToken =>
                {
                    return RefactorAsync(
                        context.Document,
                        invocation,
                        propertyName,
                        cancellationToken);
                },
                diagnostic.Id + BaseCodeFixProvider.EquivalenceKeySuffix);

            context.RegisterCodeFix(codeAction, diagnostic);
        }

        private static async Task<Document> RefactorAsync(
            Document document,
            InvocationExpressionSyntax invocation,
            string propertyName,
            CancellationToken cancellationToken)
        {
            SyntaxNode oldRoot = await document.GetSyntaxRootAsync(cancellationToken);

            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;

            memberAccess = memberAccess
                .WithName(
                    IdentifierName(propertyName)
                        .WithTriviaFrom(memberAccess.Name))
                .WithTriviaFrom(invocation);

            SyntaxNode newRoot = oldRoot.ReplaceNode(invocation, memberAccess);

            return document.WithSyntaxRoot(newRoot);
        }
    }
}
