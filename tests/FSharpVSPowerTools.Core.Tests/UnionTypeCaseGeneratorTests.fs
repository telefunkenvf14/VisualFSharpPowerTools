﻿#if INTERACTIVE
#r "../../bin/FSharp.Compiler.Service.dll"
#r "../../packages/NUnit.2.6.3/lib/nunit.framework.dll"
#load "../../src/FSharpVSPowerTools.Core/Utils.fs"
      "../../src/FSharpVSPowerTools.Core/CompilerLocationUtils.fs"
      "../../src/FSharpVSPowerTools.Core/Lexer.fs"
      "../../src/FSharpVSPowerTools.Core/LanguageService.fs"
      "../../src/FSharpVSPowerTools.Core/CodeGeneration.fs"
      "../../src/FSharpVSPowerTools.Core/InterfaceStubGenerator.fs"
#load "TestHelpers.fs"
#else
module FSharpVSPowerTools.Core.Tests.UnionTypeCaseGeneratorTests
#endif

open NUnit.Framework
open System
open System.IO
open System.Collections.Generic
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.SourceCodeServices
open FSharpVSPowerTools
open FSharpVSPowerTools.AsyncMaybe
open FSharpVSPowerTools.CodeGeneration
open Microsoft.FSharp.Compiler.Ast

let args = 
    [|
        "--noframework"; "--debug-"; "--optimize-"; "--tailcalls-"
        @"-r:C:\Program Files (x86)\Reference Assemblies\Microsoft\FSharp\.NETFramework\v4.0\4.3.0.0\FSharp.Core.dll"
        @"-r:C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5\mscorlib.dll"
        @"-r:C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5\System.dll"
        @"-r:C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5\System.Core.dll"
        @"-r:C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5\System.Drawing.dll"
        @"-r:C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5\System.Numerics.dll"
        @"-r:C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5\System.Windows.Forms.dll"
    |]

let languageService = LanguageService(fun _ -> ())
let project: ProjectOptions =
    let fileName = @"C:\file.fs"
    let projFileName = @"C:\Project.fsproj"
    let files = [| fileName |]
    { ProjectFileName = projFileName
      ProjectFileNames = [| fileName |]
      ProjectOptions = args
      ReferencedProjects = Array.empty
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UtcNow
      UnresolvedReferences = None }

type pos with
    member x.Line0: int<Line0> = LanguagePrimitives.Int32WithMeasure(x.Line - 1)
    member x.Line1: int<Line1> = LanguagePrimitives.Int32WithMeasure(x.Line)
    member x.Column0 = x.Column
    member x.Column1 = x.Column + 1

type Range = {
    StartLine: int<Line1>
    StartColumn: int
    EndLine: int<Line1>
    EndColumn: int
}
with
    static member FromSymbol(symbol: Symbol) =
        let startLine, startColumn, endLine, endColumn = symbol.Range
        { StartLine = LanguagePrimitives.Int32WithMeasure startLine
          StartColumn = startColumn
          EndLine = LanguagePrimitives.Int32WithMeasure endLine
          EndColumn = endColumn }


type MockDocument(src: string) =
    let lines =
        ResizeArray<_>(src.Split([|"\r\n"; "\n"|], StringSplitOptions.None))

    interface IDocument with
        member x.FullName = @"C:\file.fs"
        member x.GetText() = src
        member x.GetLineText0(line0: int<Line0>) = lines.[int line0]
        member x.GetLineText1(line1: int<Line1>) = lines.[int line1 - 1]

type CodeGenerationTestService() =
    interface ICodeGenerationService<ProjectOptions, pos, Range> with
        member x.GetSymbolAtPosition(_project, snapshot, pos) =
            let lineText = snapshot.GetLineText0 pos.Line0
            let src = snapshot.GetText()
            maybe {
                let! symbol =
                    Lexer.getSymbol src (int pos.Line0) (int pos.Column0) lineText args Lexer.queryLexState

                return Range.FromSymbol symbol, symbol
            }

        member x.GetSymbolAndUseAtPositionOfKind(project, snapshot, pos, kind) =
            asyncMaybe {
                let x = x :> ICodeGenerationService<_, _, _>
                let! range, symbol = x.GetSymbolAtPosition(project, snapshot, pos) |> liftMaybe
                let src = snapshot.GetText()
                let line = snapshot.GetLineText1 pos.Line1
                let! parseAndCheckResults =
                    languageService.ParseAndCheckFileInProject(project, snapshot.FullName, src, AllowStaleResults.MatchingSource)
                    |> liftAsync

                match symbol.Kind with
                | k when k = kind ->
                    // NOTE: we must set <colAtEndOfNames> = symbol.RightColumn
                    // and not <pos.Column>, otherwise GetSymbolUseAtLocation won't find it
                    let! symbolUse =
                        parseAndCheckResults.GetSymbolUseAtLocation(pos.Line, symbol.RightColumn, line, [symbol.Text])
                    return range, symbol, symbolUse
                | _ -> return! None |> liftMaybe
            }

        member x.ParseFileInProject(snapshot, projectOptions) =
            languageService.ParseFileInProject(projectOptions, snapshot.FullName, snapshot.GetText())

        member x.ExtractFSharpPos(pos) = pos

let srcToLineArray (src: string) = src.Split([|"\r\n"; "\n"|], StringSplitOptions.None)

let codeGenInfra: ICodeGenerationService<_, _, _> = upcast CodeGenerationTestService()

let asDocument (src: string) = MockDocument(src) :> IDocument
let getSymbolAtPoint (pos: pos) (document: IDocument) =
    codeGenInfra.GetSymbolAtPosition(project, document, pos)

let getSymbolAndUseAtPoint (pos: pos) (document: IDocument) =
    codeGenInfra.GetSymbolAndUseAtPositionOfKind(project, document, pos, SymbolKind.Ident)
    |> Async.RunSynchronously

//let tryFindRecordExpr (pos: pos) (snapshot: IDocument) =
//    tryFindRecordExprInBufferAtPos codeGenInfra project pos snapshot
//    |> Async.RunSynchronously

"""
type Union = Case1 | Case2

let f (x: Union) =
    match x with"""
|> asDocument
|> getSymbolAtPoint (Pos.fromZ 4 10)