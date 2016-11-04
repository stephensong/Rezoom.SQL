﻿namespace Rezoom.SQL
open System
open System.Collections.Generic
open Rezoom.SQL.InferredTypes

type IQueryTypeChecker =
    abstract member Select : SelectStmt -> InfSelectStmt

type ExprTypeChecker(cxt : ITypeInferenceContext, scope : InferredSelectScope, queryChecker : IQueryTypeChecker) =
    member this.Scope = scope
    member this.ObjectName(objectName : ObjectName) = this.ObjectName(objectName, false)
    member this.ObjectName(objectName : ObjectName, allowNotFound) : InfObjectName =
        {   SchemaName = objectName.SchemaName
            ObjectName = objectName.ObjectName
            Source = objectName.Source
            Info =
                match scope.ResolveObjectReference(objectName) with
                | Ambiguous r -> failAt objectName.Source r
                | Found f -> f
                | NotFound r ->
                    if not allowNotFound then failAt objectName.Source r
                    else Missing
        }

    member this.ColumnName(source : SourceInfo, columnName : ColumnName) =
        let tblAlias, tblInfo, name = scope.ResolveColumnReference(columnName) |> foundAt source
        {   Expr.Source = source
            Value =
                {   Table =
                        match tblAlias with
                        | None -> None
                        | Some tblAlias ->
                            {   Source = source
                                SchemaName = None
                                ObjectName = tblAlias
                                Info = TableLike tblInfo
                            } |> Some
                    ColumnName = columnName.ColumnName
                } |> ColumnNameExpr
            Info = name.Expr.Info
        }

    member this.Literal(source : SourceInfo, literal : Literal) =
        {   Expr.Source = source
            Value = LiteralExpr literal
            Info = ExprInfo<_>.OfType(InferredType.OfLiteral(literal))
        }

    member this.BindParameter(source : SourceInfo, par : BindParameter) =
        {   Expr.Source = source
            Value = BindParameterExpr par
            Info = ExprInfo<_>.OfType(cxt.Variable(par))
        }

    member this.Binary(source : SourceInfo, binary : BinaryExpr) =
        let left = this.Expr(binary.Left)
        let right = this.Expr(binary.Right)
        {   Expr.Source = source
            Value =
                {   Operator = binary.Operator
                    Left = left
                    Right = right
                } |> BinaryExpr
            Info =
                {   Type = cxt.Binary(binary.Operator, left.Info.Type, right.Info.Type) |> resultAt source
                    Aggregate = left.Info.Aggregate || right.Info.Aggregate
                    Function = None
                    Column = None
                }
        }

    member this.Unary(source : SourceInfo, unary : UnaryExpr) =
        let operand = this.Expr(unary.Operand)
        {   Expr.Source = source
            Value =
                {   Operator = unary.Operator
                    Operand = operand
                } |> UnaryExpr
            Info =
                {   Type = cxt.Unary(unary.Operator, operand.Info.Type) |> resultAt source
                    Aggregate = operand.Info.Aggregate
                    Function = None
                    Column = None
                }
        }

    member this.Cast(source : SourceInfo, cast : CastExpr) =
        let input = this.Expr(cast.Expression)
        let ty = InferredType.OfTypeName(cast.AsType, input.Info.Type)
        {   Expr.Source = source
            Value =
                {   Expression = input
                    AsType = cast.AsType
                } |> CastExpr
            Info =
                {   Type = ty
                    Aggregate = input.Info.Aggregate
                    Function = None
                    Column = None
                }
        }

    member this.Collation(source : SourceInfo, collation : CollationExpr) =
        let input = this.Expr(collation.Input)
        cxt.Unify(input.Info.Type, InferredType.String) |> resultOk source
        {   Expr.Source = source
            Value = 
                {   Input = this.Expr(collation.Input)
                    Collation = collation.Collation
                } |> CollateExpr
            Info =
                {   Type = input.Info.Type
                    Aggregate = input.Info.Aggregate
                    Function = None
                    Column = None
                }
        }

    member this.FunctionInvocation(source : SourceInfo, func : FunctionInvocationExpr) =
        match scope.Model.Builtin.Functions.TryFind(func.FunctionName) with
        | None -> failAt source <| sprintf "No such function: ``%O``" func.FunctionName
        | Some funcType ->
            let functionVars = Dictionary()
            let toInferred (ty : ArgumentType) =
                match ty with
                | ArgumentConcrete t -> ConcreteType t
                | ArgumentTypeVariable name ->
                    let succ, tvar = functionVars.TryGetValue(name)
                    if succ then tvar else
                    let avar = cxt.AnonymousVariable()
                    functionVars.[name] <- avar
                    avar
            let mutable argsAggregate = false
            let args, output =
                match func.Arguments with
                | ArgumentWildcard ->
                    if funcType.AllowWildcard then ArgumentWildcard, toInferred funcType.Output
                    else failAt source <| sprintf "Function does not permit wildcards: ``%O``" func.FunctionName
                | ArgumentList (distinct, args) ->
                    if Option.isSome distinct && not funcType.AllowDistinct then
                        failAt source <| sprintf "Function does not permit DISTINCT keyword: ``%O``" func.FunctionName
                    else
                        let outArgs = ResizeArray()
                        let add expr =
                            let arg = this.Expr(expr)
                            outArgs.Add(arg)
                            argsAggregate <- argsAggregate || arg.Info.Aggregate
                            arg.Info.Type
                        let mutable lastIndex = 0
                        for i, expectedTy in funcType.FixedArguments |> Seq.indexed do
                            if i >= args.Count then
                                failAt source <|
                                    sprintf "Function %O expects at least %d arguments but given only %d"
                                        func.FunctionName
                                        funcType.FixedArguments.Count
                                        args.Count
                            else
                                cxt.Unify(toInferred expectedTy, add args.[i]) |> resultOk args.[i].Source
                            lastIndex <- i
                        for i = lastIndex + 1 to args.Count - 1 do
                            match funcType.VariableArgument with
                            | None ->
                                failAt args.[i].Source <|
                                    sprintf "Function %O does not accept more than %d arguments"
                                        func.FunctionName
                                        funcType.FixedArguments.Count
                            | Some varArg ->
                                cxt.Unify(toInferred varArg, add args.[i]) |> resultOk args.[i].Source
                        ArgumentList (distinct, outArgs), toInferred funcType.Output
            {   Expr.Source = source
                Value = { FunctionName = func.FunctionName; Arguments = args } |> FunctionInvocationExpr
                Info =
                    {   Type = output
                        Aggregate = argsAggregate || funcType.Aggregate
                        Function = Some funcType
                        Column = None
                    }
            }

    member this.Similarity(source : SourceInfo, sim : SimilarityExpr) =
        let input = this.Expr(sim.Input)
        let pattern = this.Expr(sim.Pattern)
        let escape = Option.map this.Expr sim.Escape
        let output =
            result {
                let! inputType = cxt.Unify(input.Info.Type, StringType)
                let! patternType = cxt.Unify(pattern.Info.Type, StringType)
                match escape with
                | None -> ()
                | Some escape -> ignore <| cxt.Unify(escape.Info.Type, StringType)
                let! unified = cxt.Unify(inputType, patternType)
                return InferredType.Dependent(unified, BooleanType)
            } |> resultAt source
        {   Expr.Source = source
            Value =
                {   Invert = sim.Invert
                    Operator = sim.Operator
                    Input = input
                    Pattern = pattern
                    Escape = escape
                } |> SimilarityExpr
            Info =
                {   Type = output
                    Aggregate = input.Info.Aggregate || pattern.Info.Aggregate
                    Function = None
                    Column = None
                }
        }

    member this.Between(source : SourceInfo, between : BetweenExpr) =
        let input = this.Expr(between.Input)
        let low = this.Expr(between.Low)
        let high = this.Expr(between.High)
        {   Expr.Source = source
            Value = { Invert = between.Invert; Input = input; Low = low; High = high } |> BetweenExpr
            Info =
                {   Type = cxt.Unify([ input.Info.Type; low.Info.Type; high.Info.Type ]) |> resultAt source
                    Aggregate = input.Info.Aggregate || low.Info.Aggregate || high.Info.Aggregate
                    Function = None
                    Column = None
                }
        }

    member this.TableInvocation(table : TableInvocation) =
        {   Table = this.ObjectName(table.Table)
            Arguments = table.Arguments |> Option.map (rmap this.Expr)
        }

    member this.In(source : SourceInfo, inex : InExpr) =
        let input = this.Expr(inex.Input)
        let set =
            match inex.Set.Value with
            | InExpressions exprs ->
                let exprs = exprs |> rmap this.Expr
                seq {
                    yield input
                    yield! exprs
                } |> Seq.map (fun e -> e.Info.Type) |> cxt.Unify |> resultOk inex.Set.Source
                exprs |> InExpressions
            // TODO: enforce single column-ness?
            | InSelect select -> InSelect <| queryChecker.Select(select)
            | InTable table -> InTable <| this.TableInvocation(table)
        {   Expr.Source = source
            Value =
                {   Invert = inex.Invert
                    Input = this.Expr(inex.Input)
                    Set = { Source = inex.Set.Source; Value = set }
                } |> InExpr
            Info =
                {   Type = InferredType.Dependent(input.Info.Type, BooleanType)
                    Aggregate = input.Info.Aggregate
                    Function = None
                    Column = None
                }
        }

    member this.Case(source : SourceInfo, case : CaseExpr) =
        let case =
            {   Input = Option.map this.Expr case.Input
                Cases =
                    seq {
                        for whenExpr, thenExpr in case.Cases ->
                            this.Expr(whenExpr), this.Expr(thenExpr)
                    } |> ResizeArray
                Else =
                    {   Source = case.Else.Source
                        Value = Option.map this.Expr case.Else.Value
                    }
            }
        let outputType =
            seq {
                for _, thenExpr in case.Cases -> thenExpr.Info.Type
                match case.Else.Value with
                | None -> ()
                | Some els -> yield els.Info.Type
            } |> cxt.Unify |> resultAt source
        seq {
            yield
                match case.Input with
                | None -> InferredType.Boolean
                | Some input -> input.Info.Type
            for whenExpr, _ in case.Cases -> whenExpr.Info.Type
        } |> cxt.Unify |> resultOk source
        {   Expr.Source = source
            Value = case |> CaseExpr
            Info =
                {   Type = outputType
                    Aggregate =
                        seq {
                            match case.Input with
                            | None -> ()
                            | Some input -> yield input
                            for whenExpr, thenExpr in case.Cases do
                                yield whenExpr
                                yield thenExpr
                            match case.Else.Value with
                            | None -> ()
                            | Some els -> yield els
                        } |> Seq.exists (fun e -> e.Info.Aggregate)
                    Function = None
                    Column = None
                }
        }

    member this.Exists(source : SourceInfo, exists : SelectStmt) =
        {   Expr.Source = source
            Value = queryChecker.Select(exists) |> ExistsExpr
            Info = ExprInfo<_>.OfType(InferredType.Boolean)
        }

    member this.ScalarSubquery(source : SourceInfo, select : SelectStmt) =
        let select = queryChecker.Select(select)
        let tbl = select.Value.Info.Table.Query
        if tbl.Columns.Count <> 1 then
            failAt source <| sprintf "Scalar subquery must have 1 column (this one has %d)" tbl.Columns.Count
        {   Expr.Source = source
            Value = ScalarSubqueryExpr select
            Info = tbl.Columns.[0].Expr.Info
        }

    member this.Expr(expr : Expr) : InfExpr =
        let source = expr.Source
        match expr.Value with
        | LiteralExpr lit -> this.Literal(source, lit)
        | BindParameterExpr par -> this.BindParameter(source, par)
        | ColumnNameExpr name -> this.ColumnName(source, name)
        | CastExpr cast -> this.Cast(source, cast)
        | CollateExpr collation -> this.Collation(source, collation)
        | FunctionInvocationExpr func -> this.FunctionInvocation(source, func)
        | SimilarityExpr sim -> this.Similarity(source, sim)
        | BinaryExpr bin -> this.Binary(source, bin)
        | UnaryExpr un -> this.Unary(source, un)
        | BetweenExpr between -> this.Between(source, between)
        | InExpr inex -> this.In(source, inex)
        | ExistsExpr select -> this.Exists(source, select)
        | CaseExpr case -> this.Case(source, case)
        | ScalarSubqueryExpr select -> this.ScalarSubquery(source, select)
        | RaiseExpr raise -> { Source = source; Value = RaiseExpr raise; Info = ExprInfo<_>.OfType(InferredType.Any) }

    member this.Expr(expr : Expr, ty : CoreColumnType) =
        let expr = this.Expr(expr)
        cxt.Unify(expr.Info.Type, ty) |> resultOk expr.Source
        expr