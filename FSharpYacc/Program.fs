﻿(*
Copyright (c) 2012-2013, Jack Pappas
All rights reserved.

This code is provided under the terms of the 2-clause ("Simplified") BSD license.
See LICENSE.TXT for licensing details.
*)

namespace FSharpYacc

/// Assembly-level attributes specific to this assembly.
module private AssemblyInfo =
    open System.Reflection
    open System.Resources
    open System.Runtime.CompilerServices
    open System.Runtime.InteropServices
    open System.Security
    open System.Security.Permissions

    [<assembly: AssemblyTitle("FSharpYacc")>]
    [<assembly: AssemblyDescription("A 'yacc'-inspired parser-generator tool for F#.")>]
    [<assembly: NeutralResourcesLanguage("en-US")>]
    [<assembly: Guid("fc309105-ce95-46d1-8cb4-568fc6bea85c")>]

    (*  Makes internal modules, types, and functions visible
        to the test project so they can be unit-tested. *)
    #if DEBUG
    [<assembly: InternalsVisibleTo("FSharpYacc.Tests")>]
    #endif

    (* Dependency hints for Ngen *)
    [<assembly: DependencyAttribute("FSharp.Core", LoadHint.Always)>]
    [<assembly: DependencyAttribute("System", LoadHint.Always)>]
    [<assembly: DependencyAttribute("System.ComponentModel.Composition", LoadHint.Always)>]

    // Appease the F# compiler
    do ()


//
[<RequireQualifiedAccess>]
module Program =
    open System.ComponentModel.Composition
    open System.ComponentModel.Composition.Hosting
    open FSharp.CliArgs
    open FSharpYacc.Plugin

    (* TEMP : This code is taken from the F# Powerpack, and is licensed under the Apache 2.0 license *)
    open System.IO
    open Microsoft.FSharp.Text.Lexing
    //------------------------------------------------------------------
    // This code is duplicated from Microsoft.FSharp.Compiler.UnicodeLexing

    type private Lexbuf =  LexBuffer<char>

    /// Standard utility to create a Unicode LexBuffer
    ///
    /// One small annoyance is that LexBuffers and not IDisposable. This means
    /// we can't just return the LexBuffer object, since the file it wraps wouldn't
    /// get closed when we're finished with the LexBuffer. Hence we return the stream,
    /// the reader and the LexBuffer. The caller should dispose the first two when done.
    let private UnicodeFileAsLexbuf (filename, codePage : int option) : FileStream * StreamReader * Lexbuf =
        // Use the .NET functionality to auto-detect the unicode encoding
        // It also uses Lexing.from_text_reader to present the bytes read to the lexer in UTF8 decoded form
        let stream = new FileStream (filename, FileMode.Open, FileAccess.Read, FileShare.Read)
        let reader =
            match codePage with
            | None ->
                new StreamReader (stream, true)
            | Some n ->
                new StreamReader (stream, System.Text.Encoding.GetEncoding n)
        let lexbuf = LexBuffer.FromFunction reader.Read
        lexbuf.EndPos <- Position.FirstLine filename
        stream, reader, lexbuf
    (* End-TEMP *)


    //
    let private loadBackends () =
        //
        use catalog = new AssemblyCatalog (typeof<Backends>.Assembly)

        //
        use container = new CompositionContainer (catalog)

        //
        let backends = Backends ()
        container.ComposeParts (backends)
        backends

    /// Invokes FSharpYacc with the specified options.
    [<CompiledName("Invoke")>]
    let invoke (inputFile, options) : int =
        // Preconditions
        if inputFile = null then
            nullArg "inputFile"
        elif System.String.IsNullOrWhiteSpace inputFile then
            invalidArg "inputFile" "The path to the parser specification is empty."
        elif not <| System.IO.File.Exists inputFile then
            invalidArg "inputFile" "No parser specification exists at the specified path."

        // TEMP : This is hard-coded until we implement functionality to
        // allow the user to select which backend to use.
        let backends = loadBackends ()

        /// The parsed parser specification.
        let parserSpec =
            try
                let stream, reader, lexbuf =
                    UnicodeFileAsLexbuf (inputFile, None)
                use stream = stream
                use reader = reader
                let parserSpec = Parser.spec Lexer.token lexbuf

                // TEMP : Need to do a little massaging of the Specification for now to put some lists in the correct order.
                { parserSpec with
                    TerminalDeclarations =
                        parserSpec.TerminalDeclarations
                        |> List.map (fun (declaredType, terminals) ->
                            declaredType, List.rev terminals);
                    Productions =
                        parserSpec.Productions
                        |> List.map (fun (nonterminal, rules) ->
                            nonterminal,
                            rules
                            |> List.map (fun rule ->
                                { rule with
                                    Symbols = List.rev rule.Symbols; })
                            |> List.rev)
                        |> List.rev; }

            with ex ->
                printfn "Error: %s" ex.Message
                exit 1

        // Precompile the parsed specification to validate and process it.
        let processedSpecification, validationMessages =
            Compiler.precompile (parserSpec, options)

        // Display validation warning messages, if any.
        // TODO : Write the warning messages to NLog (or similar) instead, for flexibility.
        validationMessages.Warnings
        |> List.iter (printfn "Warning: %s")

        // If there are any validation _errors_ display them and abort compilation.
        match validationMessages.Errors with
        | (_ :: _) as errorMessages ->
            // Write the error messages to the console.
            // TODO : Write the error messages to NLog (or similar) instead, for flexibility.
            errorMessages
            |> List.iter (printfn "Error: %s")

            1   // Exit code: Error
        | [] ->
            // Compile the processed specification.
            match Compiler.compile (processedSpecification, options) with
            | Choice2Of2 errorMessages ->
                // Write the error messages to the console.
                // TODO : Write the error messages to NLog (or similar) instead, for flexibility.
                errorMessages
                |> List.iter (printfn "Error: %s")

                1   // Exit code: Error

            | Choice1Of2 parserTable ->
                // TEMP : Invoke the fsyacc-compatible backend.
                // Eventually we'll implement a way for the user to select the backend(s) to use.
                backends.FsyaccBackend.Invoke (
                    processedSpecification,
                    parserTable,
                    options)

                0   // Exit code: Success

    //
    let [<Literal>] private defaultLexerInterpreterNamespace = "Microsoft.FSharp.Text.Lexing"
    //
    let [<Literal>] private defaultParserInterpreterNamespace = "Microsoft.FSharp.Text.Parsing"
    //
    let [<Literal>] private defaultParserModuleName = "Parser"

    /// The entry point for the application.
    [<EntryPoint; CompiledName("Main")>]
    let main (options : string[]) =
        // Variables to hold parsed command-line arguments.
        let inputFile = ref None
        let generatedModuleName = ref None
        let internalModule = ref false
        //let opens = ref []
        let outputFile = ref None
        //let tokenize = ref false
        //let compat = ref false
        //let log = ref false
        //let inputCodePage = ref None
        let lexerInterpreterNamespace = ref defaultLexerInterpreterNamespace
        let parserInterpreterNamespace = ref defaultParserInterpreterNamespace

        /// Command-line options.
        let usage =
            [   ArgInfo("-o", ArgType.String (fun s -> outputFile := Some s),
                    "Name the output file.");
//                ArgInfo("-v", ArgType.Unit (fun () -> log := true),
//                    "Produce a listing file."); 
                ArgInfo("--module", ArgType.String (fun s -> generatedModuleName := Some s),
                    sprintf "The name to use for the F# module containing the generated parser. \
                     The default is '%s'." defaultParserModuleName);
                ArgInfo("--internal", ArgType.Unit (fun () -> internalModule := true),
                    "Generate an internal module");
//                ArgInfo("--open", ArgType.String (fun s -> opens := !opens @ [s]),
//                    "Add the given module to the list of those to open in both the generated signature and implementation.");
//                ArgInfo("--ml-compatibility", ArgType.Set compat,
//                    "Support the use of the global state from the 'Parsing' module in FSharp.PowerPack.dll.");
//                ArgInfo("--tokens", ArgType.Set tokenize,
//                    "Simply tokenize the specification file itself.");
//                ArgInfo("--lexlib", ArgType.String (fun s -> lexerInterpreterNamespace := s),
//                    sprintf "Specify the namespace for the implementation of the lexer table interpreter. \
//                     The default is '%s'." defaultLexerInterpreterNamespace);
//                ArgInfo("--parslib", ArgType.String (fun s -> parserInterpreterNamespace := s),
//                    sprintf "Specify the namespace for the implementation of the parser table interpreter. \
//                     The default is '%s'." defaultParserInterpreterNamespace);
//                ArgInfo("--codepage", ArgType.Int (fun i -> inputCodePage := Some i),
//                    "Assume input lexer specification file is encoded with the given codepage.");
                ]

        // Parses argument values which aren't specified by flags.
        let plainArgParser x =
            match !inputFile with
            | None ->
                inputFile := Some x
            | Some _ ->
                // If the input filename has already been set, print a message
                // to the screen, then exit with an error code.
                printfn "Error: Only one lexer specification file may be used as input."
                exit 1

        // Parse the command-line arguments.
        ArgParser.Parse (usage, plainArgParser, "fsharpyacc <filename>")

        // Validate the parsed arguments.
        // TODO

        // If the output file is not specified, use a default value.
        if Option.isNone !outputFile then
            outputFile := Some <| System.IO.Path.ChangeExtension (Option.get !inputFile, "fs")

        // Create a CompilationOptions record from the parsed arguments
        // and call the 'invoke' function with it.
        invoke (Option.get !inputFile, {
            ParserType = ParserType.Lalr1;
            // TEMP
            FsyaccBackendOptions = Some {
                OutputPath = Option.get !outputFile;
                InternalModule = !internalModule;
                ModuleName = !generatedModuleName; };
            })
        

        
