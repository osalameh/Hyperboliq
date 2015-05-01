﻿namespace Hyperboliq.Domain

module StreamGenerator =
    open Types
    open InsertExpressionPart
    open UpdateExpressionPart
    open ExpressionParts
    open Stream

    type StreamInput =
        | Delete 
        | InsertInto of InsertIntoExpression
        | InsertValues of InsertValuesExpression
        | UpdateSet of UpdateExpressionPart.UpdateExpression
        | Select of SelectExpression
        | Join of JoinExpression
        | From of FromExpression
        | Where of WhereExpression
        | GroupBy of GroupByExpression
        | OrderBy of OrderByExpression

    let HandleSelect (selectExpr  : SelectExpression) : SqlStream =
        let columnsFromExpression expr =
            expr 
            |> List.rev
            |> List.map (fun (_, col) -> col)
            |> List.concat
        let expr = columnsFromExpression selectExpr.Expression
        if selectExpr.IsDistinct then 
            Keyword(KeywordNode.Select) :: Keyword(KeywordNode.Distinct) :: expr
        else
            Keyword(KeywordNode.Select) :: expr

    let HandleFrom ({ Tables = tbls } : FromExpression) : SqlStream =
        tbls
        |> List.rev
        |> List.map (fun tref -> Table(TableToken(tref)))
        |> (fun y -> Keyword(KeywordNode.From) :: y)

    let HandleWhere ({ Clauses = whereClauses } : WhereExpression) : SqlStream =
        let FlattenWhereClause (clause : WhereClause) : WhereClauseNode = 
            match clause.JoinType with
            | ExpressionJoinType.And -> ExpressionCombinatorType.And
            | ExpressionJoinType.Or -> ExpressionCombinatorType.Or
            |> (fun x y -> { Combinator = x; Expression = y }) <| ExpressionVisitor.Visit clause.Expression clause.Tables 
        match whereClauses with
        | [] -> []
        | _ ->
            whereClauses
            |> List.rev
            |> List.map FlattenWhereClause
            |> fun list ->
                match list with
                | [] -> []
                | hd :: tl -> [ SqlNode.Where({ Start = hd.Expression; AdditionalClauses = tl }) ]

    let HandleJoin ({ Clauses = joinClauses } : JoinExpression) : SqlStream =
        match joinClauses with
        | [] -> []
        | _ -> 
            joinClauses
            |> List.rev
            |> List.map (fun c -> c.Flatten() )
            |> List.concat

    let HandleOrderBy ({ Clauses = orderByClauses } : OrderByExpression) : SqlStream =
        let FlattenOrderByClause (clause : OrderByClause) =
            let orderByExpr = ExpressionVisitor.Visit clause.Expression [ clause.Table ]
            OrderingToken(
                { 
                    Selector = orderByExpr  
                    Direction = clause.Direction
                    NullsOrdering = clause.NullsOrdering
                })

        match orderByClauses with
        | [] -> []
        | _ ->
            orderByClauses
            |> List.rev
            |> List.map FlattenOrderByClause
            |> (fun y -> Keyword(KeywordNode.OrderBy) :: y)
    
    let HandleGroupByExpression ({ Clauses = groupByClauses; Having = havingClauses } : GroupByExpression) : SqlStream =
        let HandleHavingPart (having : WhereClause list) : SqlStream =
            match having with
            | [] -> []
            | _ -> HandleWhere ({ WhereExpression.Clauses = having })
        let HandleGroupByPart (groupby : GroupByClause list) : SqlStream =
            match groupby with
            | [] -> []
            | _ ->
                groupby
                |> List.rev
                |> List.map (fun c -> ExpressionVisitor.Visit c.Expression [ c.Table ])
                |> List.concat
                |> (fun x -> Keyword(KeywordNode.GroupBy) :: x)
        [ (HandleGroupByPart groupByClauses); (HandleHavingPart havingClauses) ] |> List.concat

    let HandleInsertIntoExpression ({ Table = tbl; Columns = cols } : InsertIntoExpression) : SqlStream =
        Keyword(KeywordNode.InsertInto) :: [ InsertHead({ Table = TableToken(tbl); Columns = cols }) ]

    let HandleInsertValuesExpression (expr : InsertValuesExpression) : SqlStream =
        Keyword(KeywordNode.Values) :: (List.map (fun v -> InsertValue(v)) expr.Values |> List.rev)

    let HandleUpdateSet ({ Table = tbl; SetExpressions = exprs } : UpdateExpressionPart.UpdateExpression) : SqlStream =
        let HandleSetExpression (se : UpdateExpressionPart.UpdateSetExpression) : UpdateSetToken =
            { Column = se.Column; Value = se.Value; }
        [ UpdateStatementHead({ Table = tbl; SetExpressions = (List.map HandleSetExpression exprs |> List.rev) }) ]

    let Handle (part : StreamInput) : SqlStream =
        match part with
        | Delete -> [ Keyword(KeywordNode.Delete) ]
        | InsertInto expr -> HandleInsertIntoExpression expr
        | InsertValues expr -> HandleInsertValuesExpression expr
        | UpdateSet expr -> HandleUpdateSet expr
        | Select expr -> HandleSelect expr
        | Join expr -> HandleJoin expr
        | From expr -> HandleFrom expr
        | Where expr -> HandleWhere expr
        | GroupBy expr -> HandleGroupByExpression expr
        | OrderBy expr -> HandleOrderBy expr

    let GenerateStream parts =
        parts
        |> List.ofSeq
        |> List.map (fun part -> Handle part)
        |> List.concat

