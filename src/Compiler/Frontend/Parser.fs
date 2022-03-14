module Compiler.Frontend.Parser

open FParsec

open Utils
open Compiler.Frontend.Syntax

type Parser<'a> = Parser<'a, unit>

let (<!>) f a = a |>> f
let (<*>) f a = pipe2 f a (<|)

// Misc Parsers

let singleLineComment = pstring "--" >>. skipRestOfLine true
let multiLineComment = pstring "{-" >>. skipManyTill anyChar (pstring "-}")
let endLine =
  let r = skipChar '\r'
  let n = skipChar '\n'
  choice [attempt (r >>. n); r; n]

let ws1 = skipMany1 (choice [skipChar ' '; skipChar '\t'; multiLineComment])
let ws = ws1 <|>% ()

let wsMulti = skipMany (choice [singleLineComment; endLine; ws1])
let stmtSep = choice [skipChar ';'; singleLineComment; endLine; eof] >>. wsMulti

let comma = pchar ',' .>> ws
let equals = pchar '=' .>> ws
let colon = pchar ':' .>> ws

let choice' ps = choice <| List.map attempt ps

// Exprs

let boolLit =
  choice [
    stringReturn "False" (U <| BoolLit false)
    stringReturn "True" (U <| BoolLit true)
  ]

let numberFormat =
  NumberLiteralOptions.AllowMinusSign
  ||| NumberLiteralOptions.AllowFraction
  ||| NumberLiteralOptions.AllowExponent

let numLit =
  numberLiteral numberFormat "numeric literal" |>> fun nl ->
    if nl.IsInteger then
      U << IntLit <| int nl.String
    else
      U << FloatLit <| float nl.String

let stringLit: Parser<_> =
  let normalChar = satisfy (fun c -> c <> '\\' && c <> '"')

  let unescape = function
    | 'n' -> '\n'
    | 'r' -> '\r'
    | 't' -> '\t'
    | c -> c

  let escapedChar = pchar '\\' >>. (anyOf "\\nrt\"" |>> unescape)

  between (pchar '"') (pchar '"') (manyChars <| choice [normalChar; escapedChar])
  |>> (StringLit >> U)

let expr, exprRef = createParserForwardedToRef ()

let listLit =
  between (pchar '[' .>> ws) (pchar ']' .>> ws) (sepBy expr comma)
  |>> (ListLit >> U)

let reserved =
  [ "let"; "end"; "if"; "then"; "else"; "while"; "do"; "for"; "down"; "to"
    "and"; "or"; "not"; "read"; "write"; "length"; "break"; "continue"
    "push"; "pop"; "Bool"; "Int"; "Float"; "String"; "True"; "False"
  ]

let ident =
  let fstChar c = c = '_' || c = '\'' || isLetter c
  let sndChar c = fstChar c || isDigit c

  many1Satisfy2 fstChar sndChar >>= fun s ->
    if List.contains s reserved then
      fail $"Reserved identifier \"{s}\""
    else
      preturn s

let var = ident |>> (Var >> U)

let parensExpr = between (pchar '(' .>> ws) (pchar ')' .>> ws) expr
let exprSimple = choice' [boolLit; numLit; stringLit; listLit; var; parensExpr] .>> ws

let withSubscript e =
  let subscript = between (pchar '[' .>> ws) (pchar ']' .>> ws) expr
  let unrollSubscript = List.fold <| curry (Subscript >> U)

  unrollSubscript <!> e <*> many subscript

let ws' = ws >>. notFollowedByL (pchar '-') "minus"

let opp = OperatorPrecedenceParser ()
opp.TermParser <- withSubscript exprSimple

let ops =
  let arith = curry3 Arith
  let comp = curry3 Comp
  let logic = curry3 Logic

  [ ["and", logic And; "or", logic Or], Associativity.Right
    ["==", comp Eq; "!=", comp Neq; "<", comp Lt; "<=", comp Lte; ">", comp Gt; ">=", comp Gte], Associativity.None
    ["+", arith Add; "-", arith Sub], Associativity.Left
    ["*", arith Mul; "/", arith Div; "%", arith Mod], Associativity.Left
    ["^", curry Pow; "<>", curry Append], Associativity.Right
  ]

let infixPrec = List.length ops + 1

opp.AddOperator(PrefixOperator("-", ws', infixPrec, true, Negate >> U))
opp.AddOperator(PrefixOperator("not", ws', infixPrec, true, Not >> U))
opp.AddOperator(PrefixOperator("read", ws', infixPrec, true, Read >> U))
opp.AddOperator(PrefixOperator("length", ws', infixPrec, true, Length >> U))

for prec, (ops, assoc) in List.indexed ops do
  for (opStr, op) in ops do
    opp.AddOperator(InfixOperator(opStr, ws', prec + 1, assoc, op >.. U))

exprRef.Value <- opp.ExpressionParser

// Types

let primType =
  choice [
    stringReturn "Bool" Bool
    stringReturn "Int" Int
    stringReturn "Float" Float
    stringReturn "String" String
  ]

let type', typeRef = createParserForwardedToRef ()

let listType = between (pchar '[' .>> ws) (pchar ']' .>> ws) type' |>> List
typeRef.Value <- choice' [listType; primType] .>> ws

// Stmts

let lvalue = withSubscript (var .>> ws)

let stmt, stmtRef = createParserForwardedToRef ()

let let' =
  curry4 Let
  <!> (pstring "let" >>. ws >>. ident .>> ws)
  <*> opt (colon >>. type')
  <*> (equals >>. expr)
  <*> (stmtSep >>. stmt)

let assign = curry Assign <!> (lvalue .>> equals) <*> (expr .>> stmtSep)

let push =
  curry Push
  <!> (pstring "push" >>. ws >>. lvalue)
  <*> many1 (comma >>. expr) .>> stmtSep

let pop = pstring "pop" >>. ws >>. lvalue .>> stmtSep |>> Pop

let write = pstring "write" >>. ws >>. sepBy1 expr comma .>> stmtSep |>> Write

let break' = pstring "break" >>. ws >>. stmtSep >>% Break
let continue' = pstring "continue" >>. ws >>. stmtSep >>% Continue

let end' = pstring "end" >>. ws >>. stmtSep

let if' =
  let if' = pstring "if" >>. ws
  let then' = pstring "then" >>. ws
  let else' = pstring "else" >>. ws

  let elseIf =
    (else' >>. if' >>. expr) .>>. (then' >>. stmtSep >>. stmt)

  let rec unrollIf c t es e = If (c, t, List.foldBack (uncurry (curry3 If)) es e)

  unrollIf
  <!> (if' >>. expr)
  <*> (then' >>. stmtSep >>. stmt)
  <*> many (attempt elseIf)
  <*> ((else' >>. stmtSep >>. stmt) <|>% Nop .>> end')

let while' =
  curry While
  <!> (pstring "while" >>. ws >>. expr)
  <*> (pstring "do" >>. ws >>. stmtSep >>. stmt .>> end')

let doWhile =
  curry DoWhile
  <!> (pstring "do" >>. ws >>. stmtSep >>. stmt)
  <*> (pstring "while" >>. ws >>. expr .>> stmtSep)

let for' =
  curry5 For
  <!> (pstring "for" >>. ws >>. ident .>> ws)
  <*> (equals >>. expr)
  <*> ((pstring "down" >>. ws >>% true) <|>% false)
  <*> (pstring "to" >>. ws >>. expr)
  <*> (pstring "do" >>. ws >>. stmtSep >>. stmt .>> end')

let stmtSimple =
  choice' [let'; doWhile; for'; while'; if'; write; push; pop; assign; break'; continue']

stmtRef.Value <- many stmtSimple |>> foldr (curry Seq) Nop

let program: Stmt<Id, UExpr> Parser = wsMulti >>. stmt .>> eof

let parse (name, code) =
  match runParserOnString program () name code with
  | Success (a, _, _) -> Result.Ok a
  | Failure (e, _, _) -> Result.Error ("Parser error:\n" + e)
