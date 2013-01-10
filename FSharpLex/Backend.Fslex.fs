﻿(*
Copyright (c) 2012-2013, Jack Pappas
All rights reserved.

This code is provided under the terms of the 2-clause ("Simplified") BSD license.
See LICENSE.TXT for licensing details.
*)

namespace FSharpLex.Plugin

open System.ComponentModel.Composition
open System.IO
open FSharpLex
open FSharpLex.SpecializedCollections
open FSharpLex.Ast
open FSharpLex.Compile


(* TODO :   In the code-generation backends below, where the user-defined semantic actions
            are emitted, it might be useful to add a bit of code which emits a single-line
            comment before emitting the semantic action code when the action will never be
            executed because that action's pattern is overlapped by some earlier pattern.
            E.g., "This code is unreachable because it's pattern will never be matched."
            This would just serve as a reminder to the user later on (after the code is generated)
            in case they don't see the warning message we emit. *)

/// Emit table-driven code which is compatible to the code
/// generated by the older 'fslex' tool.
[<RequireQualifiedAccess>]
module private FsLex =
    open System.CodeDom.Compiler
    open System.Text
    open LanguagePrimitives
    open BackendUtils.CodeGen

    (* TODO :   Given that each rule is compiled as it's own DFA and therefore won't ever transition
                into a state from another rule, we might be able to drastically shrink the size of the
                generated table by creating non-zero-based arrays for the transition arrays of each state.
                This way, the transition array for each state only needs to include transitions to the
                states in that DFA, but since the base index of the array will be set to the same index
                that the starting state (for that DFA) would have in the full transition table the indexing
                used within the interpreter will still work correctly.
                Note, however, that the .NET CLR doesn't eliminate array bounds checks for accesses into
                non-zero-based arrays, so while this technique would shrink the size of the table, it will
                also introduce some performance penalty. *)

    //
    let [<Literal>] private interpreterVariableName = "_fslex_tables"
    //
    let [<Literal>] private transitionTableVariableName = "trans"
    //
    let [<Literal>] private actionTableVariableName = "actions"
    //
    let [<Literal>] private sentinelValue = System.UInt16.MaxValue
    //
    let [<Literal>] private lexerBufferVariableName = "lexbuf"
    //
    let [<Literal>] private lexerBufferTypeName = "Microsoft.FSharp.Text.Lexing.LexBuffer<_>"
    //
    let [<Literal>] private lexingStateVariableName = "_fslex_state"

    //
    let private asciiTransitionVectorElements (compiledRule, ruleDfaStateId, baseDfaStateId, indentingWriter : IndentedTextWriter) =
        (*  The transition vector for each state in an 'fslex'-compatible ASCII transition
            table has 257 elements. The first 256 elements represent each possible ASCII value;
            the last element represents the 'end-of-file' marker. *)

        // Emit the transition vector elements, based on the transitions out of this state.
        let ruleDfaTransitions = compiledRule.Dfa.Transitions
        for c = 0 to 255 do
            let targetStateId =
                // Determine the id of the state we transition to when this character is the input.
                // OPTIMIZE : This lookup is *really* slow -- we should create an optimized
                // lookup table on-the-fly while compiling the DFA.
                let targetStateId =
                    ruleDfaTransitions.AdjacencyMap
                    |> Map.tryPick (fun edgeKey edgeSet ->
                        if edgeKey.Source = ruleDfaStateId &&
                            CharSet.contains (char c) edgeSet then
                            // Add the starting state of this rule to the relative DFA state id
                            // to get the DFA state id for the combined DFA table.
                            Some (edgeKey.Target + baseDfaStateId)
                        else None)

                // If no transition edge was found for this character, return the
                // sentinel value to indicate there's no transition.
                defaultArg targetStateId (Int32WithMeasure <| int sentinelValue)

            // Emit the state number of the transition target.
            sprintf "%uus; " (Checked.uint16 targetStateId)
            |> indentingWriter.Write

        // Emit the element representing the state to transition
        // into when the 'end-of'file' marker is consumed.
        // NOTE : Only the initial DFA state of a rule can consume the EOF marker!
        let eofTransitionTarget =
            if compiledRule.Dfa.InitialState = ruleDfaStateId then
                match ruleDfaTransitions.EofTransition with
                | None -> sentinelValue
                | Some edgeKey ->
                    // Remember the target DFA state is _relative_ to this DFA --
                    // add it to the base DFA state id to get it's state id for the combined DFA.
                    Checked.uint16 (edgeKey.Target + baseDfaStateId)
            else sentinelValue

        sprintf "%uus; " eofTransitionTarget
        |> indentingWriter.Write

    //
    let private unicodeTransitionVectorElements (compiledRule, ruleDfaStateId, baseDfaStateId, indentingWriter : IndentedTextWriter) =
        raise <| System.NotImplementedException "unicodeTransitionVectorElements"
        ()

    //
    let private transitionAndActionTables (compiledRules : Map<RuleIdentifier, CompiledRule>) (options : CompilationOptions) (indentingWriter : IndentedTextWriter) =
        /// The combined number of DFA states in all of the DFAs.
        let combinedDfaStateCount =
            (0, compiledRules)
            ||> Map.fold (fun combinedDfaStateCount _ compiledRule ->
                combinedDfaStateCount + compiledRule.Dfa.Transitions.VertexCount)

        /// The set of all valid input characters.
        let allValidInputChars =
            // OPTIMIZE : This could be determined on-the-fly while compiling the DFA
            // so we don't have to perform a costly additional computation here.
            (CharSet.empty, compiledRules)
            ||> Map.fold (fun allValidInputChars _ compiledRule ->
                (allValidInputChars, compiledRule.Dfa.Transitions.AdjacencyMap)
                ||> Map.fold (fun allValidInputChars _ edgeSet ->
                    CharSet.union allValidInputChars edgeSet))

        /// The maximum character value accepted by the combined DFA.
        let maxCharValue = CharSet.maxElement allValidInputChars

        // Emit the 'let' binding for the fslex "Tables" object.
        "/// Interprets the transition and action tables of the lexer automaton."
        |> indentingWriter.WriteLine

        sprintf "let private %s =" interpreterVariableName
        |> indentingWriter.WriteLine

        // Indent the body of the "let" binding.
        IndentedTextWriter.indented indentingWriter <| fun indentingWriter ->

        // Documentation comments for the transition table.
        "/// <summary>Transition table.</summary>" |> indentingWriter.WriteLine
        "/// <remarks>" |> indentingWriter.WriteLine
        "/// The state number is the first index (i.e., the index of the outer array)." |> indentingWriter.WriteLine
        "/// The value of the next character (expanded to an integer) in the input stream is the second index." |> indentingWriter.WriteLine
        "/// Given a state number and a character value, this table returns the state number of" |> indentingWriter.WriteLine
        "/// the next state to transition to." |> indentingWriter.WriteLine
        "/// </remarks>" |> indentingWriter.WriteLine

        // Emit the "let" binding for the transition table.
        sprintf "let %s : uint16[] array =" transitionTableVariableName
        |> indentingWriter.WriteLine

        // Indent the body of the "let" binding for the transition table.
        IndentedTextWriter.indented indentingWriter <| fun indentingWriter ->
            // Opening bracket of the array.
            indentingWriter.WriteLine "[|"

            // Emit the transition vector for each state in the combined DFA.
            (0, compiledRules)
            ||> Map.fold (fun baseDfaStateId ruleId compiledRule ->
                // Emit a comment with the name of the rule.
                sprintf "(*** Rule: %s ***)" ruleId
                |> indentingWriter.WriteLine

                let ruleDfaTransitions = compiledRule.Dfa.Transitions
                let ruleDfaStateCount = ruleDfaTransitions.VertexCount

                // Write the transition vectors for the states in this rule's DFA.
                for ruleDfaStateId = 0 to ruleDfaStateCount - 1 do
                    // Emit a comment with the state number (in the overall combined DFA).
                    sprintf "(* State %i *)" <| baseDfaStateId + ruleDfaStateId
                    |> indentingWriter.WriteLine

                    // Emit the opening bracket of the transition vector for this state.
                    indentingWriter.Write "[| "

                    // Emit the transition vector elements, based on the transitions out of this state.
                    // In 'fslex', the length of the transition vector depends on whether
                    // or not the lexer is generated with support for Unicode.
                    if options.Unicode then
                        unicodeTransitionVectorElements (
                            compiledRule,
                            Int32WithMeasure ruleDfaStateId,
                            Int32WithMeasure baseDfaStateId,
                            indentingWriter)
                    else
                        asciiTransitionVectorElements (
                            compiledRule,
                            Int32WithMeasure ruleDfaStateId,
                            Int32WithMeasure baseDfaStateId,
                            indentingWriter)

                    // Emit the closing bracket of the transition vector for this state,
                    // plus a semicolon to separate it from the next state's transition vector.
                    indentingWriter.WriteLine "|];"

                // Advance to the next rule.
                baseDfaStateId + ruleDfaStateCount)
            // Discard the state id accumulator, it's no longer needed.
            |> ignore

            // Closing bracket of the array.
            indentingWriter.WriteLine "|]"

        // Emit a newline before emitting the action table.
        indentingWriter.WriteLine ()

        // Documentation comments for the action table.
        "/// <summary>The action table.</summary>" |> indentingWriter.WriteLine

        // Emit the "let" binding for the action table.
        sprintf "let %s : uint16[] = [| " actionTableVariableName
        |> indentingWriter.WriteLine

        // Indent the body of the "let" binding for the action table.
        IndentedTextWriter.indented indentingWriter <| fun indentingWriter ->
            (0, compiledRules)
            ||> Map.fold (fun ruleStartingStateId ruleId compiledRule ->
                // Write a comment with the name of this rule.
                sprintf "(*** Rule: %s ***)" ruleId
                |> indentingWriter.WriteLine

                let ruleDfaTransitions = compiledRule.Dfa.Transitions
                /// The number of states in this rule's DFA.
                let ruleDfaStateCount = ruleDfaTransitions.VertexCount

                for dfaStateId = 0 to ruleDfaStateCount - 1 do
                    // Determine the index of the rule clause accepted by this DFA state (if any).
                    let acceptedRuleClauseIndex =
                        compiledRule.Dfa.RuleAcceptedByState
                        |> Map.tryFind (LanguagePrimitives.Int32WithMeasure dfaStateId)

                    // Emit the accepted rule number.
                    match acceptedRuleClauseIndex with
                    | None ->
                        // Emit the sentinel value which indicates this is not a final (accepting) state.
                        sentinelValue.ToString () + "us; "
                    | Some ruleClauseIndex ->
                        // Emit the rule-clause index.
                        ruleClauseIndex.ToString () + "us; "
                    |> indentingWriter.Write

                // End the line containing the transition elements for this rule.
                indentingWriter.WriteLine ()

                // Update the starting state ID for the next rule.
                ruleStartingStateId + ruleDfaStateCount)
            // Discard the threaded state ID counter
            |> ignore

            // Emit the closing bracket for the array.
            indentingWriter.WriteLine "|]"

        // Emit a newline before emitting the code to create the interpreter object.
        indentingWriter.WriteLine ()

        // Emit code to create the interpreter object.
        "// Create the interpreter from the transition and action tables."
        |> indentingWriter.WriteLine

        sprintf "Microsoft.FSharp.Text.Lexing.%sTables.Create (%s, %s)"
            (if options.Unicode then "Unicode" else "Ascii")
            transitionTableVariableName
            actionTableVariableName
        |> indentingWriter.WriteLine

    /// Emits the code for the functions which execute the semantic actions of the rules.
    let private ruleFunctions (compiledRules : Map<RuleIdentifier, CompiledRule>) (indentingWriter : IndentedTextWriter) =
        ((0, true), compiledRules)
        ||> Map.fold (fun (ruleStartingStateId, isFirstRule) ruleId compiledRule ->
            // Emit a comment with the name of this rule.
            sprintf "(* Rule: %s *)" ruleId
            |> indentingWriter.WriteLine

            // Emit the let-binding for this rule's function.
            sprintf "%s %s "
                (if isFirstRule then "let rec" else "and")
                ruleId
            |> indentingWriter.Write

            // Emit parameter names
            compiledRule.Parameters
            |> Array.iter (fun paramName ->
                indentingWriter.Write paramName
                indentingWriter.Write ' ')            

            // Emit the lexer buffer parameter as the last (right-most) parameter.
            sprintf "(%s : %s) ="
                lexerBufferVariableName
                lexerBufferTypeName
            |> indentingWriter.WriteLine

            // Indent and emit the body of the function.
            IndentedTextWriter.indented indentingWriter <| fun indentingWriter ->
                // Emit the "let" binding for the inner function.
                sprintf "let _fslex_%s " ruleId
                |> indentingWriter.Write

                // Emit parameter names
                compiledRule.Parameters
                |> Array.iter (fun paramName ->
                    indentingWriter.Write paramName
                    indentingWriter.Write ' ')

                // Emit the lexer-state and lexer buffer parameters.
                sprintf "%s %s =" lexingStateVariableName lexerBufferVariableName
                |> indentingWriter.WriteLine

                // Indent and emit the body of the inner function, which is essentially
                // a big "match" statement which calls the user-defined semantic actions.
                IndentedTextWriter.indented indentingWriter <| fun indentingWriter ->
                    // Emit the top of the "match" statement.
                    sprintf "match %s.Interpret (%s, %s) with"
                        interpreterVariableName
                        lexingStateVariableName
                        lexerBufferVariableName
                    |> indentingWriter.WriteLine

                    // Emit the match patterns (which are just the indices of the rules),
                    // and within them emit the user-defined semantic action code.
                    compiledRule.RuleClauseActions
                    |> Array.iteri (fun ruleClauseIndex actionCode ->
                        // Emit the index as a match pattern.
                        "| " + ruleClauseIndex.ToString() + " ->"
                        |> indentingWriter.Write    // 'Write', not 'WriteLine' (see comment below).

                        // Decrease the indentation down to one (1) when emitting the user's code.
                        // Due to a small bug in IndentedTextWriter, a change in indentation only
                        // takes effect after WriteLine() is called. Therefore, we emit the newline
                        // for the match pattern after the indent level has been changed, so the
                        // indentation takes effect "immediately".
                        IndentedTextWriter.atIndentLevel 1 indentingWriter <| fun indentingWriter ->
                            // Emit the newline for the match pattern.
                            indentingWriter.WriteLine ()

                            // Emit the user-defined code for this pattern's semantic action.
                            // This has to be done line-by-line so the indenting is correct!
                            // OPTIMIZE : Speed this up a bit by using a fold or 'for' loop
                            // to traverse the string, checking for newlines and writing each
                            // non-newline character into 'indentingWriter'.
                            actionCode.Split (
                                [|"\r\n";"\r";"\n"|],
                                System.StringSplitOptions.None)
                            |> Array.iter indentingWriter.WriteLine)

                    // Emit a catch-all pattern to handle possible errors.
                    indentingWriter.WriteLine "| invalidAction ->"
                    IndentedTextWriter.indented indentingWriter <| fun indentingWriter ->
                        sprintf "failwithf \"Invalid action index (%%i) specified for the '%s' lexer rule.\" invalidAction" (string ruleId)
                        |> indentingWriter.WriteLine

                // Emit a newline before emitting the call to the inner function.
                indentingWriter.WriteLine ()

                // Emit the call to the inner function.
                sprintf "_fslex_%s " ruleId
                |> indentingWriter.Write
                
                compiledRule.Parameters
                |> Array.iter (fun paramName ->
                    indentingWriter.Write paramName
                    indentingWriter.Write ' ')
                
                sprintf "%i %s"
                    (ruleStartingStateId + int compiledRule.Dfa.InitialState)
                    lexerBufferVariableName
                |> indentingWriter.WriteLine

                // Emit a newline before emitting the next rule's function.
                indentingWriter.WriteLine ()

            // Update the starting state ID for the next rule.
            ruleStartingStateId + compiledRule.Dfa.Transitions.VertexCount,
            // The "isFirstRule" flag is always false after the first rule is emitted.
            false)
        // Discard the flag
        |> ignore

    //
    let emit (compiledSpec : CompiledSpecification, options : CompilationOptions) (writer : #TextWriter) : unit =
        // Preconditions
        if writer = null then
            nullArg "writer"

        /// Used to create properly-formatted code.
        use indentingWriter = new IndentedTextWriter (writer, "    ")

        // Emit the header (if present).
        compiledSpec.Header
        |> Option.iter indentingWriter.WriteLine

        // Emit a newline before emitting the table-driven code.
        indentingWriter.WriteLine ()

        // Emit the transition/action table for the DFA.
        transitionAndActionTables compiledSpec.CompiledRules options indentingWriter
        assert (indentingWriter.Indent = 0) // Make sure indentation was reset

        // Emit a newline before emitting the semantic action functions.
        indentingWriter.WriteLine ()

        // Emit the semantic functions for the rules.
        ruleFunctions compiledSpec.CompiledRules indentingWriter
        assert (indentingWriter.Indent = 0) // Make sure indentation was reset

        // Emit a newline before emitting the footer.
        indentingWriter.WriteLine ()

        // Emit the footer (if present).
        compiledSpec.Footer
        |> Option.iter indentingWriter.WriteLine


/// A backend which emits code implementing a table-based pattern matcher
/// compatible with 'fslex' and the table interpreters in the F# PowerPack.
[<Export(typeof<IBackend>)>]
type FslexBackend () =
    interface IBackend with
        member this.EmitCompiledSpecification (compiledSpec, options) : unit =
            /// Compilation options specific to this backend.
            let fslexOptions =
                match options.FslexBackendOptions with
                | None ->
                    raise <| exn "No backend-specific options were provided."
                | Some options ->
                    options

            // Generate the code and write it to the specified file.
            using (File.CreateText fslexOptions.OutputPath) (FsLex.emit (compiledSpec, options))
