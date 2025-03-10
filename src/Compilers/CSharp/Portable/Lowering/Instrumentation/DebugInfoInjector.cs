﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// This type is responsible for adding debugging sequence points for the executable code.
    /// It can be combined with other <see cref="Instrumenter"/>s. Usually, this class should be 
    /// the root of the chain in order to ensure sound debugging experience for the instrumented code.
    /// In other words, sequence points are typically applied after all other changes.
    /// </summary>
    internal partial class DebugInfoInjector : CompoundInstrumenter
    {
        /// <summary>
        /// A singleton object that performs only one type of instrumentation - addition of debugging sequence points. 
        /// </summary>
        private static readonly DebugInfoInjector s_singleton = new DebugInfoInjector(NoOp);

        private DebugInfoInjector(Instrumenter previous)
            : base(previous)
        {
        }

        public static DebugInfoInjector Create(Instrumenter previous)
            => (previous == NoOp) ? s_singleton : new DebugInfoInjector(previous);

        protected override CompoundInstrumenter WithPreviousImpl(Instrumenter previous)
            => Create(previous);

        public override BoundStatement InstrumentNoOpStatement(BoundNoOpStatement original, BoundStatement rewritten)
        {
            return AddSequencePoint(base.InstrumentNoOpStatement(original, rewritten));
        }

        public override BoundStatement InstrumentBreakStatement(BoundBreakStatement original, BoundStatement rewritten)
        {
            return AddSequencePoint(base.InstrumentBreakStatement(original, rewritten));
        }

        public override BoundStatement InstrumentContinueStatement(BoundContinueStatement original, BoundStatement rewritten)
        {
            return AddSequencePoint(base.InstrumentContinueStatement(original, rewritten));
        }

        public override BoundStatement InstrumentExpressionStatement(BoundExpressionStatement original, BoundStatement rewritten)
        {
            rewritten = base.InstrumentExpressionStatement(original, rewritten);

            if (original.IsConstructorInitializer())
            {
                switch (original.Syntax.Kind())
                {
                    case SyntaxKind.ConstructorDeclaration:
                        // This is an implicit constructor initializer.
                        var decl = (ConstructorDeclarationSyntax)original.Syntax;
                        return new BoundSequencePointWithSpan(decl, rewritten, CreateSpanForConstructorInitializer(decl));
                    case SyntaxKind.BaseConstructorInitializer:
                    case SyntaxKind.ThisConstructorInitializer:
                        var init = (ConstructorInitializerSyntax)original.Syntax;
                        Debug.Assert(init.Parent is object);
                        return new BoundSequencePointWithSpan(init, rewritten, CreateSpanForConstructorInitializer((ConstructorDeclarationSyntax)init.Parent));
                }
            }

            return AddSequencePoint(rewritten);
        }

        public override BoundStatement InstrumentFieldOrPropertyInitializer(BoundStatement original, BoundStatement rewritten)
        {
            rewritten = base.InstrumentFieldOrPropertyInitializer(original, rewritten);
            SyntaxNode syntax = original.Syntax;

            if (rewritten.Kind == BoundKind.Block)
            {
                var block = (BoundBlock)rewritten;
                return block.Update(block.Locals, block.LocalFunctions, block.HasUnsafeModifier, ImmutableArray.Create(InstrumentFieldOrPropertyInitializer(block.Statements.Single(), syntax)));
            }

            return InstrumentFieldOrPropertyInitializer(rewritten, syntax);
        }

        private static BoundStatement InstrumentFieldOrPropertyInitializer(BoundStatement rewritten, SyntaxNode syntax)
        {
            if (syntax.IsKind(SyntaxKind.Parameter))
            {
                // This is an initialization of a generated property based on record parameter.
                return AddSequencePoint(rewritten);
            }

            Debug.Assert(syntax is { Parent: { Parent: { } } });
            var grandparent = syntax.Parent.Parent;
            switch (grandparent.Kind())
            {
                case SyntaxKind.VariableDeclarator:
                    var declaratorSyntax = (VariableDeclaratorSyntax)grandparent;
                    return AddSequencePoint(declaratorSyntax, rewritten);

                case SyntaxKind.PropertyDeclaration:
                    var declaration = (PropertyDeclarationSyntax)grandparent;
                    return AddSequencePoint(declaration, rewritten);

                default:
                    throw ExceptionUtilities.UnexpectedValue(grandparent.Kind());
            }
        }

        public override BoundStatement InstrumentGotoStatement(BoundGotoStatement original, BoundStatement rewritten)
        {
            return AddSequencePoint(base.InstrumentGotoStatement(original, rewritten));
        }

        public override BoundStatement InstrumentThrowStatement(BoundThrowStatement original, BoundStatement rewritten)
        {
            return AddSequencePoint(base.InstrumentThrowStatement(original, rewritten));
        }

        public override BoundStatement InstrumentYieldBreakStatement(BoundYieldBreakStatement original, BoundStatement rewritten)
        {
            rewritten = base.InstrumentYieldBreakStatement(original, rewritten);

            if (original.WasCompilerGenerated && original.Syntax.Kind() == SyntaxKind.Block)
            {
                // implicit yield break added by the compiler
                return new BoundSequencePointWithSpan(original.Syntax, rewritten, ((BlockSyntax)original.Syntax).CloseBraceToken.Span);
            }

            return AddSequencePoint(rewritten);
        }

        public override BoundStatement InstrumentYieldReturnStatement(BoundYieldReturnStatement original, BoundStatement rewritten)
        {
            return AddSequencePoint(base.InstrumentYieldReturnStatement(original, rewritten));
        }

        public override void InstrumentBlock(BoundBlock original, LocalRewriter rewriter, ref TemporaryArray<LocalSymbol> additionalLocals, out BoundStatement? prologue, out BoundStatement? epilogue)
        {
            base.InstrumentBlock(original, rewriter, ref additionalLocals, out var previousPrologue, out var previousEpilogue);

            prologue = previousPrologue;
            epilogue = previousEpilogue;

            if (original.Syntax is BlockSyntax blockSyntax && !original.WasCompilerGenerated)
            {
                prologue = new BoundSequencePointWithSpan(original.Syntax, previousPrologue, blockSyntax.OpenBraceToken.Span);

                // no need to mark "}" on the outermost block
                // as it cannot leave it normally. The block will have "return" at the end.
                SyntaxNode? parent = original.Syntax.Parent;
                if (parent == null || !(parent.IsAnonymousFunction() || parent is BaseMethodDeclarationSyntax))
                {
                    epilogue = new BoundSequencePointWithSpan(original.Syntax, previousEpilogue, blockSyntax.CloseBraceToken.Span);
                }
            }
            else if (original == rewriter.CurrentMethodBody)
            {
                if (previousPrologue != null)
                {
                    prologue = BoundSequencePoint.CreateHidden(previousPrologue);
                }
                else if (rewriter.Factory.TopLevelMethod is SynthesizedSimpleProgramEntryPointSymbol)
                {
                    prologue = BoundSequencePoint.CreateHidden();
                }

                if (previousEpilogue != null)
                {
                    epilogue = BoundSequencePoint.CreateHidden(previousEpilogue);
                }
            }
        }

        public override BoundExpression InstrumentDoStatementCondition(BoundDoStatement original, BoundExpression rewrittenCondition, SyntheticBoundNodeFactory factory)
        {
            // EnC: We need to insert a hidden sequence point to handle function remapping in case 
            // the containing method is edited while methods invoked in the condition are being executed.
            return AddConditionSequencePoint(base.InstrumentDoStatementCondition(original, rewrittenCondition, factory), original.Syntax, factory);
        }

        public override BoundExpression InstrumentWhileStatementCondition(BoundWhileStatement original, BoundExpression rewrittenCondition, SyntheticBoundNodeFactory factory)
        {
            // EnC: We need to insert a hidden sequence point to handle function remapping in case 
            // the containing method is edited while methods invoked in the condition are being executed.
            return AddConditionSequencePoint(base.InstrumentWhileStatementCondition(original, rewrittenCondition, factory), original.Syntax, factory);
        }

        public override BoundStatement InstrumentDoStatementConditionalGotoStart(BoundDoStatement original, BoundStatement ifConditionGotoStart)
        {
            var doSyntax = (DoStatementSyntax)original.Syntax;
            var span = TextSpan.FromBounds(
                doSyntax.WhileKeyword.SpanStart,
                doSyntax.SemicolonToken.Span.End);

            return new BoundSequencePointWithSpan(doSyntax, base.InstrumentDoStatementConditionalGotoStart(original, ifConditionGotoStart), span);
        }

        public override BoundStatement InstrumentWhileStatementConditionalGotoStartOrBreak(BoundWhileStatement original, BoundStatement ifConditionGotoStart)
        {
            WhileStatementSyntax whileSyntax = (WhileStatementSyntax)original.Syntax;
            TextSpan conditionSequencePointSpan = TextSpan.FromBounds(
                whileSyntax.WhileKeyword.SpanStart,
                whileSyntax.CloseParenToken.Span.End);

            return new BoundSequencePointWithSpan(whileSyntax, base.InstrumentWhileStatementConditionalGotoStartOrBreak(original, ifConditionGotoStart), conditionSequencePointSpan);
        }

        /// <summary>
        /// Add sequence point |here|:
        /// 
        /// foreach (Type var in |expr|) { }
        /// </summary>
        /// <remarks>
        /// Hit once, before looping begins.
        /// </remarks>
        public override BoundStatement InstrumentForEachStatementCollectionVarDeclaration(BoundForEachStatement original, BoundStatement? collectionVarDecl)
        {
            var forEachSyntax = (CommonForEachStatementSyntax)original.Syntax;
            return new BoundSequencePoint(forEachSyntax.Expression,
                                          base.InstrumentForEachStatementCollectionVarDeclaration(original, collectionVarDecl));
        }

        public override BoundStatement InstrumentForEachStatementDeconstructionVariablesDeclaration(BoundForEachStatement original, BoundStatement iterationVarDecl)
        {
            var forEachSyntax = (ForEachVariableStatementSyntax)original.Syntax;
            return new BoundSequencePointWithSpan(forEachSyntax, base.InstrumentForEachStatementDeconstructionVariablesDeclaration(original, iterationVarDecl), forEachSyntax.Variable.Span);
        }

        /// <summary>
        /// Add sequence point |here|:
        /// 
        /// |foreach| (Type var in expr) { }
        /// </summary>
        /// <remarks>
        /// Hit once, before looping begins.
        /// </remarks>
        public override BoundStatement InstrumentForEachStatement(BoundForEachStatement original, BoundStatement rewritten)
        {
            var forEachSyntax = (CommonForEachStatementSyntax)original.Syntax;
            var span = forEachSyntax.AwaitKeyword != default
                ? TextSpan.FromBounds(forEachSyntax.AwaitKeyword.Span.Start, forEachSyntax.ForEachKeyword.Span.End)
                : forEachSyntax.ForEachKeyword.Span;

            var foreachKeywordSequencePoint = new BoundSequencePointWithSpan(forEachSyntax, null, span);
            return new BoundStatementList(forEachSyntax,
                                            ImmutableArray.Create<BoundStatement>(foreachKeywordSequencePoint,
                                                                                base.InstrumentForEachStatement(original, rewritten)));
        }

        /// <summary>
        /// Add sequence point |here|:
        /// 
        /// foreach (|Type var| in expr) { }
        /// </summary>
        /// <remarks>
        /// Hit every iteration.
        /// </remarks>
        public override BoundStatement InstrumentForEachStatementIterationVarDeclaration(BoundForEachStatement original, BoundStatement iterationVarDecl)
        {
            TextSpan iterationVarDeclSpan;
            switch (original.Syntax.Kind())
            {
                case SyntaxKind.ForEachStatement:
                    {
                        var forEachSyntax = (ForEachStatementSyntax)original.Syntax;
                        iterationVarDeclSpan = TextSpan.FromBounds(forEachSyntax.Type.SpanStart, forEachSyntax.Identifier.Span.End);
                        break;
                    }
                case SyntaxKind.ForEachVariableStatement:
                    {
                        var forEachSyntax = (ForEachVariableStatementSyntax)original.Syntax;
                        iterationVarDeclSpan = forEachSyntax.Variable.Span;
                        break;
                    }
                default:
                    throw ExceptionUtilities.UnexpectedValue(original.Syntax.Kind());
            }
            return new BoundSequencePointWithSpan(original.Syntax,
                                                  base.InstrumentForEachStatementIterationVarDeclaration(original, iterationVarDecl),
                                                  iterationVarDeclSpan);
        }

        public override BoundStatement InstrumentForStatementConditionalGotoStartOrBreak(BoundForStatement original, BoundStatement branchBack)
        {
            // hidden sequence point if there is no condition
            return BoundSequencePoint.Create(original.Condition?.Syntax,
                                            base.InstrumentForStatementConditionalGotoStartOrBreak(original, branchBack));
        }

        public override BoundStatement InstrumentForEachStatementConditionalGotoStart(BoundForEachStatement original, BoundStatement branchBack)
        {
            var syntax = (CommonForEachStatementSyntax)original.Syntax;
            return new BoundSequencePointWithSpan(syntax,
                                                  base.InstrumentForEachStatementConditionalGotoStart(original, branchBack),
                                                  syntax.InKeyword.Span);
        }

        public override BoundExpression InstrumentForStatementCondition(BoundForStatement original, BoundExpression rewrittenCondition, SyntheticBoundNodeFactory factory)
        {
            // EnC: We need to insert a hidden sequence point to handle function remapping in case 
            // the containing method is edited while methods invoked in the condition are being executed.
            return AddConditionSequencePoint(base.InstrumentForStatementCondition(original, rewrittenCondition, factory), original.Syntax, factory);
        }

        public override BoundStatement InstrumentIfStatement(BoundIfStatement original, BoundStatement rewritten)
        {
            var syntax = (IfStatementSyntax)original.Syntax;
            return new BoundSequencePointWithSpan(
                syntax,
                base.InstrumentIfStatement(original, rewritten),
                TextSpan.FromBounds(
                    syntax.IfKeyword.SpanStart,
                    syntax.CloseParenToken.Span.End),
                original.HasErrors);
        }

        public override BoundExpression InstrumentIfStatementCondition(BoundIfStatement original, BoundExpression rewrittenCondition, SyntheticBoundNodeFactory factory)
        {
            // EnC: We need to insert a hidden sequence point to handle function remapping in case 
            // the containing method is edited while methods invoked in the condition are being executed.
            return AddConditionSequencePoint(base.InstrumentIfStatementCondition(original, rewrittenCondition, factory), original.Syntax, factory);
        }

        public override BoundStatement InstrumentLabelStatement(BoundLabeledStatement original, BoundStatement rewritten)
        {
            var labeledSyntax = (LabeledStatementSyntax)original.Syntax;
            var span = TextSpan.FromBounds(labeledSyntax.Identifier.SpanStart, labeledSyntax.ColonToken.Span.End);
            return new BoundSequencePointWithSpan(labeledSyntax,
                                                  base.InstrumentLabelStatement(original, rewritten),
                                                  span);
        }

        public override BoundStatement InstrumentLocalInitialization(BoundLocalDeclaration original, BoundStatement rewritten)
        {
            return AddSequencePoint(original.Syntax.Kind() == SyntaxKind.VariableDeclarator ?
                                        (VariableDeclaratorSyntax)original.Syntax :
                                        ((LocalDeclarationStatementSyntax)original.Syntax).Declaration.Variables.First(),
                                    base.InstrumentLocalInitialization(original, rewritten));
        }

        public override BoundStatement InstrumentLockTargetCapture(BoundLockStatement original, BoundStatement lockTargetCapture)
        {
            LockStatementSyntax lockSyntax = (LockStatementSyntax)original.Syntax;
            return new BoundSequencePointWithSpan(lockSyntax,
                                                  base.InstrumentLockTargetCapture(original, lockTargetCapture),
                                                  TextSpan.FromBounds(lockSyntax.LockKeyword.SpanStart, lockSyntax.CloseParenToken.Span.End));
        }

        public override BoundStatement InstrumentReturnStatement(BoundReturnStatement original, BoundStatement rewritten)
        {
            rewritten = base.InstrumentReturnStatement(original, rewritten);

            if (original.WasCompilerGenerated && original.ExpressionOpt == null && original.Syntax.Kind() == SyntaxKind.Block)
            {
                // implicit return added by the compiler
                return new BoundSequencePointWithSpan(original.Syntax, rewritten, ((BlockSyntax)original.Syntax).CloseBraceToken.Span);
            }

            return new BoundSequencePoint(original.Syntax, rewritten);
        }

        public override BoundStatement InstrumentSwitchStatement(BoundSwitchStatement original, BoundStatement rewritten)
        {
            SwitchStatementSyntax switchSyntax = (SwitchStatementSyntax)original.Syntax;
            TextSpan switchSequencePointSpan = TextSpan.FromBounds(
                switchSyntax.SwitchKeyword.SpanStart,
                (switchSyntax.CloseParenToken != default) ? switchSyntax.CloseParenToken.Span.End : switchSyntax.Expression.Span.End);

            return new BoundSequencePointWithSpan(
                syntax: switchSyntax,
                statementOpt: base.InstrumentSwitchStatement(original, rewritten),
                span: switchSequencePointSpan,
                hasErrors: false);
        }

        public override BoundStatement InstrumentSwitchWhenClauseConditionalGotoBody(BoundExpression original, BoundStatement ifConditionGotoBody)
        {
            WhenClauseSyntax? whenClause = original.Syntax.FirstAncestorOrSelf<WhenClauseSyntax>();
            Debug.Assert(whenClause != null);

            return new BoundSequencePointWithSpan(
                syntax: whenClause,
                statementOpt: base.InstrumentSwitchWhenClauseConditionalGotoBody(original, ifConditionGotoBody),
                span: whenClause.Span);
        }

        public override BoundStatement InstrumentUsingTargetCapture(BoundUsingStatement original, BoundStatement usingTargetCapture)
        {
            return AddSequencePoint((UsingStatementSyntax)original.Syntax,
                                    base.InstrumentUsingTargetCapture(original, usingTargetCapture));
        }

        public override BoundExpression InstrumentCatchClauseFilter(BoundCatchBlock original, BoundExpression rewrittenFilter, SyntheticBoundNodeFactory factory)
        {
            rewrittenFilter = base.InstrumentCatchClauseFilter(original, rewrittenFilter, factory);

            // EnC: We need to insert a hidden sequence point to handle function remapping in case 
            // the containing method is edited while methods invoked in the condition are being executed.
            CatchFilterClauseSyntax? filterClause = ((CatchClauseSyntax)original.Syntax).Filter;
            Debug.Assert(filterClause is { });
            return AddConditionSequencePoint(new BoundSequencePointExpression(filterClause, rewrittenFilter, rewrittenFilter.Type), filterClause, factory);
        }

        public override BoundExpression InstrumentSwitchStatementExpression(BoundStatement original, BoundExpression rewrittenExpression, SyntheticBoundNodeFactory factory)
        {
            // EnC: We need to insert a hidden sequence point to handle function remapping in case 
            // the containing method is edited while methods invoked in the expression are being executed.
            return AddConditionSequencePoint(base.InstrumentSwitchStatementExpression(original, rewrittenExpression, factory), original.Syntax, factory);
        }

        public override BoundExpression InstrumentSwitchExpressionArmExpression(BoundExpression original, BoundExpression rewrittenExpression, SyntheticBoundNodeFactory factory)
        {
            return new BoundSequencePointExpression(original.Syntax, base.InstrumentSwitchExpressionArmExpression(original, rewrittenExpression, factory), rewrittenExpression.Type);
        }

        public override BoundStatement InstrumentSwitchBindCasePatternVariables(BoundStatement bindings)
        {
            // Mark the code that binds pattern variables to their values as hidden.
            // We do it to tell that this is not a part of previous statement.
            return BoundSequencePoint.CreateHidden(base.InstrumentSwitchBindCasePatternVariables(bindings));
        }
    }
}
