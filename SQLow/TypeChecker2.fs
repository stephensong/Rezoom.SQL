﻿module SQLow.TypeChecker2
open System
open System.Collections.Generic
open SQLow.InferredTypes

type ITypeInferenceContext with
    member typeInference.Unify(inferredType, coreType : CoreColumnType) =
        typeInference.Unify(inferredType, InferredType.Dependent(inferredType, coreType))
    member typeInference.Unify(inferredType, resultType : Result<InferredType, string>) =
        match resultType with
        | Ok t -> typeInference.Unify(inferredType, t)
        | Error _ as e -> e
    member typeInference.Unify(types : InferredType seq) =
        types
        |> Seq.fold
            (function | Ok s -> (fun t -> typeInference.Unify(s, t)) | Error _ as e -> (fun _ -> e))
            (Ok InferredType.Any)
    member typeInference.Concrete(inferred) = typeInference.Concrete(inferred)
    member typeInference.Binary(op, left, right) =
        match op with
        | Concatenate -> typeInference.Unify([ left; right; InferredType.String ])
        | Multiply
        | Divide
        | Add
        | Subtract -> typeInference.Unify([ left; right; InferredType.Number ])
        | Modulo
        | BitShiftLeft
        | BitShiftRight
        | BitAnd
        | BitOr -> typeInference.Unify([ left; right; InferredType.Integer ])
        | LessThan
        | LessThanOrEqual
        | GreaterThan
        | GreaterThanOrEqual
        | Equal
        | NotEqual
        | Is
        | IsNot ->
            result {
                let! operandType = typeInference.Unify(left, right)
                return InferredType.Dependent(operandType, BooleanType)
            }
        | And
        | Or -> typeInference.Unify([ left; right; InferredType.Boolean ])
    member typeInference.Unary(op, operandType) =
        match op with
        | Negative
        | BitNot -> typeInference.Unify(operandType, InferredType.Number)
        | Not -> typeInference.Unify(operandType, InferredType.Boolean)
        | IsNull
        | NotNull -> result { return InferredType.Boolean }

type TypeChecker2(cxt : ITypeInferenceContext, scope : InferredSelectScope) =
    member this.ObjectName(objectName : ObjectName) : InfObjectName =
        {   SchemaName = objectName.SchemaName
            ObjectName = objectName.ObjectName
            Source = objectName.Source
            Info = scope.ResolveObjectReference(objectName) |> foundAt objectName.Source
        }
    member this.ColumnName(source : SourceInfo, columnName : ColumnName) =
        let name = scope.ResolveColumnReference(columnName) |> foundAt source
        {   Source = source
            Value =
                {   Table = Option.map this.ObjectName columnName.Table
                    ColumnName = columnName.ColumnName
                } |> ColumnNameExpr
            Info = name.Expr.Info
        }
    member this.Literal(source : SourceInfo, literal : Literal) =
        {   Source = source
            Value = LiteralExpr literal
            Info = ExprInfo<_>.OfType(InferredType.OfLiteral(literal))
        }
    member this.BindParameter(source : SourceInfo, par : BindParameter) =
        {   Source = source
            Value = BindParameterExpr par
            Info = ExprInfo<_>.OfType(cxt.Variable(par))
        }

    member this.Binary(source : SourceInfo, binary : BinaryExpr) =
        let left = this.Expr(binary.Left)
        let right = this.Expr(binary.Right)
        {   Source = source
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
        {   Source = source
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
        {   Source = source
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
        {   Source = source
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
            {   Source = source
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
        {   Source = source
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
        {   Source = source
            Value = { Invert = between.Invert; Input = input; Low = low; High = high } |> BetweenExpr
            Info =
                {   Type = cxt.Unify([ input.Info.Type; low.Info.Type; high.Info.Type ]) |> resultAt source
                    Aggregate = input.Info.Aggregate || low.Info.Aggregate || high.Info.Aggregate
                    Function = None
                    Column = None
                }
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
            | InSelect select -> InSelect <| this.Select(select)
            | InTable table -> InTable <| this.TableInvocation(table)
        {   Source = source
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
        {   Source = source
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
        {   Source = source
            Value = this.Select(exists) |> ExistsExpr
            Info = ExprInfo<_>.OfType(InferredType.Boolean)
        }
    member this.ScalarSubquery(source : SourceInfo, select : SelectStmt) =
        let select = this.Select(select)
        let tbl = select.Value.Info.Table.Query
        if tbl.Columns.Count <> 1 then
            failAt source <| sprintf "Scalar subquery must have 1 column (this one has %d)" tbl.Columns.Count
        {   Source = source
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
    member this.TableInvocation(table : TableInvocation) =
        {   Table = this.ObjectName(table.Table)
            Arguments = table.Arguments |> Option.map (rmap this.Expr)
        }
    member this.CTE(cte : CommonTableExpression) =
        {   Name = cte.Name
            ColumnNames = cte.ColumnNames
            AsSelect = this.Select(cte.AsSelect)
        }
    member this.WithClause(withClause : WithClause) =
        {   Recursive = withClause.Recursive
            Tables = rmap this.CTE withClause.Tables
        }
    member this.OrderingTerm(orderingTerm : OrderingTerm) =
        {   By = this.Expr(orderingTerm.By)
            Direction = orderingTerm.Direction
        }
    member this.Limit(limit : Limit) =
        {   Limit = this.Expr(limit.Limit)
            Offset = Option.map this.Expr limit.Offset
        }
    member this.ResultColumn(resultColumn : ResultColumn) =
        match resultColumn with
        | ColumnsWildcard -> ColumnsWildcard
        | TableColumnsWildcard tbl -> TableColumnsWildcard (this.ObjectName(tbl))
        | Column (expr, alias) -> Column (this.Expr(expr), alias)
    member this.ResultColumns(resultColumns : ResultColumns) =
        {   Distinct = resultColumns.Distinct
            Columns = resultColumns.Columns
            |> rmap (fun { Source = source; Value = value } -> { Source = source; Value = this.ResultColumn(value) })
        }
    member this.TableOrSubquery(table : TableOrSubquery) =
        match table with
        | Table (tinvoc, alias, index) ->
            Table (this.TableInvocation(tinvoc), alias, index)
        | Subquery (select, alias) ->
            Subquery (this.Select(select), alias)
    member this.JoinConstraint(constr : JoinConstraint) =  
        match constr with
        | JoinOn expr -> JoinOn <| this.Expr(expr)
        | JoinUsing names -> JoinUsing names
        | JoinUnconstrained -> JoinUnconstrained
    member this.Join(join : Join) =
        {   JoinType = join.JoinType
            LeftTable = this.TableExpr(join.LeftTable)
            RightTable = this.TableExpr(join.RightTable)
            Constraint = this.JoinConstraint(join.Constraint)
        }
    member this.TableExpr(table : TableExpr) =
        {   Source = table.Source
            Value =
                match table.Value with
                | TableOrSubquery sub -> TableOrSubquery <| this.TableOrSubquery(sub)
                | Join join -> Join <| this.Join(join)
        }
    member this.GroupBy(groupBy : GroupBy) =
        {   By = groupBy.By |> rmap this.Expr
            Having = groupBy.Having |> Option.map this.Expr
        }
    member this.SelectCore(select : SelectCore) =
        {   Columns = this.ResultColumns(select.Columns)
            From = Option.map this.TableExpr select.From
            Where = Option.map this.Expr select.Where
            GroupBy = Option.map this.GroupBy select.GroupBy
        }
    member this.CompoundTerm(term : CompoundTerm) : InfCompoundTerm =
        {   Source = term.Source
            Value =
                match term.Value with
                | Values vals ->
                    Values (vals |> rmap (fun w -> { Value = rmap this.Expr w.Value; Source = w.Source }))
                | Select select ->
                    Select <| this.SelectCore(select)
        }
    member this.Compound(compound : CompoundExpr) =
        {   CompoundExpr.Source = compound.Source
            Value = 
                match compound.Value with
                | CompoundTerm term -> CompoundTerm <| this.CompoundTerm(term)
                | Union (expr, term) -> Union (this.Compound(expr), this.CompoundTerm(term))
                | UnionAll (expr, term) -> UnionAll (this.Compound(expr), this.CompoundTerm(term))
                | Intersect (expr, term) -> Intersect (this.Compound(expr), this.CompoundTerm(term))
                | Except (expr, term) -> Except (this.Compound(expr), this.CompoundTerm(term))
        }
    member this.Select(select : SelectStmt) : InfSelectStmt =
        {   Source = select.Source
            Value =
                let select = select.Value
                {   With = Option.map this.WithClause select.With
                    Compound = this.Compound(select.Compound)
                    OrderBy = Option.map (rmap this.OrderingTerm) select.OrderBy
                    Limit = Option.map this.Limit select.Limit
                }
        }
    member this.ForeignKey(foreignKey) =
        {   ReferencesTable = this.ObjectName(foreignKey.ReferencesTable)
            ReferencesColumns = foreignKey.ReferencesColumns
            Rules = foreignKey.Rules
            Defer = foreignKey.Defer
        }
    member this.ColumnConstraint(constr : ColumnConstraint) =
        {   Name = constr.Name
            ColumnConstraintType =
                match constr.ColumnConstraintType with
                | NullableConstraint -> NullableConstraint
                | PrimaryKeyConstraint clause -> PrimaryKeyConstraint clause
                | NotNullConstraint clause -> NotNullConstraint clause
                | UniqueConstraint conflict -> UniqueConstraint conflict
                | CheckConstraint expr -> CheckConstraint <| this.Expr(expr)
                | DefaultConstraint def -> DefaultConstraint <| this.Expr(def)
                | CollateConstraint name -> CollateConstraint name
                | ForeignKeyConstraint foreignKey -> ForeignKeyConstraint <| this.ForeignKey(foreignKey)
        }
    member this.ColumnDef(cdef : ColumnDef) =
        {   Name = cdef.Name
            Type = cdef.Type
            Constraints = rmap this.ColumnConstraint cdef.Constraints
        }
    member this.Alteration(alteration : AlterTableAlteration) =
        match alteration with
        | RenameTo name -> RenameTo name
        | AddColumn cdef -> AddColumn <| this.ColumnDef(cdef)
    member this.CreateIndex(createIndex : CreateIndexStmt) =
        {   Unique = createIndex.Unique
            IfNotExists = createIndex.IfNotExists
            IndexName = this.ObjectName(createIndex.IndexName)
            TableName = this.ObjectName(createIndex.TableName)
            IndexedColumns = createIndex.IndexedColumns |> rmap (fun (e, d) -> this.Expr(e), d)
            Where = createIndex.Where |> Option.map this.Expr
        }
    member this.TableIndexConstraint(constr : TableIndexConstraintClause) =
        {   Type = constr.Type
            IndexedColumns = constr.IndexedColumns |> rmap (fun (e, d) -> this.Expr(e), d)
            ConflictClause = constr.ConflictClause
        }
    member this.TableConstraint(constr : TableConstraint) =
        {   Name = constr.Name
            TableConstraintType =
                match constr.TableConstraintType with
                | TableIndexConstraint clause ->
                    TableIndexConstraint <| this.TableIndexConstraint(clause)
                | TableForeignKeyConstraint (names, foreignKey) ->
                    TableForeignKeyConstraint (names, this.ForeignKey(foreignKey))
                | TableCheckConstraint expr -> TableCheckConstraint <| this.Expr(expr)
        }
    member this.CreateTableDefinition(createTable : CreateTableDefinition) =
        {   Columns = createTable.Columns |> rmap this.ColumnDef
            Constraints = createTable.Constraints |> rmap this.TableConstraint
            WithoutRowId = createTable.WithoutRowId
        }
    member this.CreateTable(createTable : CreateTableStmt) =
        {   Temporary = createTable.Temporary
            IfNotExists = createTable.IfNotExists
            Name = { Source = createTable.Name.Source; Value = this.ObjectName(createTable.Name.Value) }
            As =
                match createTable.As with
                | CreateAsSelect select -> CreateAsSelect <| this.Select(select)
                | CreateAsDefinition def -> CreateAsDefinition <| this.CreateTableDefinition(def)
        }
    member this.TriggerAction(action : TriggerAction) =
        match action with
        | TriggerUpdate update -> TriggerUpdate <| this.Update(update)
        | TriggerInsert insert -> TriggerInsert <| this.Insert(insert)
        | TriggerDelete delete -> TriggerDelete <| this.Delete(delete)
        | TriggerSelect select -> TriggerSelect <| this.Select(select)
    member this.CreateTrigger(createTrigger : CreateTriggerStmt) =
        {   Temporary = createTrigger.Temporary
            IfNotExists = createTrigger.IfNotExists
            TriggerName = this.ObjectName(createTrigger.TriggerName)
            TableName = this.ObjectName(createTrigger.TableName)
            Schedule = createTrigger.Schedule
            Cause = createTrigger.Cause
            Condition = Option.map this.Expr createTrigger.Condition
            Actions = rmap this.TriggerAction createTrigger.Actions
        }
    member this.CreateView(createView : CreateViewStmt) =
        {   Temporary = createView.Temporary
            IfNotExists = createView.IfNotExists
            ViewName = this.ObjectName(createView.ViewName)
            ColumnNames = createView.ColumnNames
            AsSelect = this.Select(createView.AsSelect)
        }
    member this.CreateVirtualTable(createVirtual : CreateVirtualTableStmt) =
        {   IfNotExists = createVirtual.IfNotExists
            VirtualTable = this.ObjectName(createVirtual.VirtualTable)
            UsingModule = createVirtual.UsingModule
            WithModuleArguments = createVirtual.WithModuleArguments
        }
    member this.QualifiedTableName(qualified : QualifiedTableName) =
        {   TableName = this.ObjectName(qualified.TableName)
            IndexHint = qualified.IndexHint
        }
    member this.Delete(delete : DeleteStmt) =
        {   With = Option.map this.WithClause delete.With
            DeleteFrom = this.QualifiedTableName(delete.DeleteFrom)
            Where = Option.map this.Expr delete.Where
            OrderBy = Option.map (rmap this.OrderingTerm) delete.OrderBy
            Limit = Option.map this.Limit delete.Limit
        }
    member this.DropObject(drop : DropObjectStmt) =
        {   Drop = drop.Drop
            IfExists = drop.IfExists
            IndexName = this.ObjectName(drop.IndexName)
        }
    member this.Insert(insert : InsertStmt) =
        {   With = Option.map this.WithClause insert.With
            Or = insert.Or
            InsertInto = this.ObjectName(insert.InsertInto)
            Columns = insert.Columns
            Data = Option.map this.Select insert.Data
        }
    member this.Pragma(pragma : PragmaStmt) =
        {   Pragma = this.ObjectName(pragma.Pragma)
            Value = pragma.Value
        }
    member this.Update(update : UpdateStmt) =
        {   With = Option.map this.WithClause update.With
            UpdateTable = this.QualifiedTableName(update.UpdateTable)
            Or = update.Or
            Set = update.Set |> rmap (fun (name, expr) -> name, this.Expr(expr))
            Where = Option.map this.Expr update.Where
            OrderBy = Option.map (rmap this.OrderingTerm) update.OrderBy
            Limit = Option.map this.Limit update.Limit
        }
    member this.Stmt(stmt : Stmt) =
        match stmt with
        | AlterTableStmt alter ->
            AlterTableStmt <|
                {   Table = this.ObjectName(alter.Table)
                    Alteration = this.Alteration(alter.Alteration)
                }
        | AnalyzeStmt objectName -> AnalyzeStmt <| Option.map this.ObjectName objectName
        | AttachStmt (expr, name) -> AttachStmt (this.Expr(expr), name)
        | BeginStmt ttype -> BeginStmt ttype
        | CommitStmt -> CommitStmt
        | CreateIndexStmt index -> CreateIndexStmt <| this.CreateIndex(index)
        | CreateTableStmt createTable -> CreateTableStmt <| this.CreateTable(createTable)
        | CreateTriggerStmt createTrigger -> CreateTriggerStmt <| this.CreateTrigger(createTrigger)
        | CreateViewStmt createView -> CreateViewStmt <| this.CreateView(createView)
        | CreateVirtualTableStmt createVirtual -> CreateVirtualTableStmt <| this.CreateVirtualTable(createVirtual)
        | DeleteStmt delete -> DeleteStmt <| this.Delete(delete)
        | DetachStmt name -> DetachStmt name
        | DropObjectStmt drop -> DropObjectStmt <| this.DropObject(drop)
        | InsertStmt insert -> InsertStmt <| this.Insert(insert)
        | PragmaStmt pragma -> PragmaStmt <| this.Pragma(pragma)
        | ReindexStmt name -> ReindexStmt <| Option.map this.ObjectName name
        | ReleaseStmt name -> ReleaseStmt name
        | RollbackStmt stmt -> RollbackStmt stmt
        | SavepointStmt save -> SavepointStmt save
        | SelectStmt select -> SelectStmt <| this.Select(select)
        | ExplainStmt stmt -> ExplainStmt <| this.Stmt(stmt)
        | UpdateStmt update -> UpdateStmt <| this.Update(update)
        | VacuumStmt -> VacuumStmt