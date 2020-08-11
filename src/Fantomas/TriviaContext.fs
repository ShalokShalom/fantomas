module internal Fantomas.TriviaContext

open Fantomas
open Fantomas.Context
open Fantomas.TriviaTypes
open FSharp.Compiler.Range
open FSharp.Compiler.SyntaxTree

let tokN (range: range) (tokenName: FsTokenType) f =
    enterNodeTokenByName range tokenName +> f +> leaveNodeTokenByName range tokenName

let firstNewlineOrComment (es: SynExpr list) (ctx: Context) =
    let triviaNodes =
        [ yield! (Map.tryFindOrEmptyList SynExpr_App ctx.TriviaMainNodes)
          yield! (Map.tryFindOrEmptyList SynExpr_DoBang ctx.TriviaMainNodes)
          yield! (Map.tryFindOrEmptyList SynExpr_YieldOrReturnFrom ctx.TriviaMainNodes) ]

    es
    |> List.tryHead
    |> Option.bind (fun e -> TriviaHelpers.findByRange triviaNodes e.Range)
    |> fun cb ->
        match cb with
        | Some ({ ContentBefore = (TriviaContent.Newline|TriviaContent.Comment _) as head ::rest } as tn) ->
            let mapNode t = if t = tn then { tn with ContentBefore = rest } else t

            let updatedTriviaMainNodes =
                match tn.Type with
                | MainNode(mn) ->
                    let nodes = Map.find mn ctx.TriviaMainNodes
                    List.map mapNode nodes
                    |> fun newNodes -> Map.add mn newNodes ctx.TriviaMainNodes
                | _ -> ctx.TriviaMainNodes

            let ctx' = { ctx with TriviaMainNodes = updatedTriviaMainNodes }
            printTriviaContent head ctx'
        | _ -> sepNone ctx

let triviaAfterArrow (range: range) (ctx: Context) =
    let hasCommentAfterArrow =
        findTriviaTokenFromName RARROW range ctx
        |> Option.bind (fun t ->
            t.ContentAfter
            |> List.tryFind (function | Comment(LineCommentAfterSourceCode(_)) -> true | _ -> false)
        )
        |> Option.isSome
    ((tokN range RARROW sepArrow) +> ifElse hasCommentAfterArrow sepNln sepNone) ctx

let ``else if / elif`` (rangeOfIfThenElse: range) (ctx: Context) =
    let keywords =
        [ yield! (Map.tryFindOrEmptyList ELSE ctx.TriviaTokenNodes)
          yield! (Map.tryFindOrEmptyList IF ctx.TriviaTokenNodes)
          yield! (Map.tryFindOrEmptyList ELIF ctx.TriviaTokenNodes) ]
        |> List.sortBy (fun tn -> tn.Range.StartLine, tn.Range.StartColumn)
        |> TriviaHelpers.``keyword token inside range`` rangeOfIfThenElse
        |> List.map (fun (tok, t) -> (TokenParser.getFsToken tok.TokenInfo.TokenName, t))

    let resultExpr =
        match keywords with
        | (ELSE, elseTrivia)::(IF, ifTrivia)::_ ->
            let commentAfterElseKeyword = TriviaHelpers.``has line comment after`` elseTrivia
            let commentAfterIfKeyword = TriviaHelpers.``has line comment after`` ifTrivia
            let triviaBeforeIfKeyword =
                (Map.tryFindOrEmptyList SynExpr_IfThenElse ctx.TriviaMainNodes) // ctx.Trivia
                |> List.filter (fun t ->
                        RangeHelpers.``range contains`` rangeOfIfThenElse t.Range
                        && (RangeHelpers.``range after`` elseTrivia.Range t.Range))
                |> List.tryHead

            tokN rangeOfIfThenElse ELSE (!- "else") +>
            ifElse commentAfterElseKeyword sepNln sepSpace +>
            opt sepNone triviaBeforeIfKeyword printContentBefore +>
            tokN rangeOfIfThenElse IF (!- "if ") +>
            ifElse commentAfterIfKeyword (indent +> sepNln) sepNone

        | (ELIF,elifTok)::_
        | [(ELIF,elifTok)] ->
            let commentAfterElIfKeyword = TriviaHelpers.``has line comment after`` elifTok
            tokN rangeOfIfThenElse ELIF (!- "elif ")
            +> ifElse commentAfterElIfKeyword (indent +> sepNln) sepNone

        | [] ->
            // formatting from AST
            !- "else if "

        | _ ->
            failwith "Unexpected scenario when formatting else if / elif, please open an issue via https://jindraivanek.gitlab.io/fantomas-ui"

    resultExpr ctx