﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

module Microsoft.Quantum.CsharpGeneration.Testing.EntryPoint

open System
open System.Collections.Immutable
open System.Globalization
open System.IO
open System.Reflection
open System.Threading.Tasks
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.Quantum.QsCompiler.CompilationBuilder
open Microsoft.Quantum.QsCompiler.CsharpGeneration
open Microsoft.Quantum.QsCompiler.DataTypes
open Microsoft.VisualStudio.LanguageServer.Protocol
open Xunit


/// The path to the Q# file that provides the Microsoft.Quantum.Core namespace.
let private coreFile = Path.Combine ("Circuits", "Core.qs") |> Path.GetFullPath

/// The path to the Q# file that contains the test cases.
let private testFile = Path.Combine ("Circuits", "EntryPointTests.qs") |> Path.GetFullPath

/// The namespace used for the test cases.
let private testNamespace = "Microsoft.Quantum.CsharpGeneration.Testing.EntryPoint"

/// The test cases.
let private tests = File.ReadAllText testFile |> fun text -> text.Split "// ---"

/// Compiles Q# source code into a syntax tree with the list of entry points names.
let private compileQsharp source =
    let uri name = Uri ("file://" + name)
    let fileManager name content =
        CompilationUnitManager.InitializeFileManager (uri name, content)

    use compilationManager = new CompilationUnitManager (isExecutable = true)
    let fileManagers = ImmutableHashSet.Create (fileManager coreFile (File.ReadAllText coreFile),
                                                fileManager testFile source)
    compilationManager.AddOrUpdateSourceFilesAsync fileManagers |> ignore
    let compilation = compilationManager.Build ()
    let errors =
        compilation.Diagnostics ()
        |> Seq.filter (fun diagnostic -> diagnostic.Severity = DiagnosticSeverity.Error)
    Assert.Empty errors
    compilation.BuiltCompilation.Namespaces, compilation.BuiltCompilation.EntryPoints

/// Generates C# source code for the given test case number.
let private generateCsharp testNum =
    let syntaxTree, entryPoints = compileQsharp tests.[testNum - 1]
    let context = CodegenContext.Create syntaxTree
    let entryPoint = context.allCallables.[Seq.exactlyOne entryPoints]
    [
        SimulationCode.generate (NonNullable<_>.New testFile) context
        EntryPoint.generate context entryPoint
    ]

/// The full path to a referenced assembly given its short name.
let private referencedAssembly name =
    (AppContext.GetData "TRUSTED_PLATFORM_ASSEMBLIES" :?> string).Split ';'
    |> Seq.find (fun path -> String.Equals (Path.GetFileNameWithoutExtension path, name,
                                            StringComparison.InvariantCultureIgnoreCase))

/// Compiles the C# sources into an assembly.
let private compileCsharp (sources : string seq) =
    let references : MetadataReference list =
        [
            "netstandard"
            "System.Collections.Immutable"
            "System.CommandLine"
            "System.Console"
            "System.Linq"
            "System.Private.CoreLib"
            "System.Runtime"
            "System.Runtime.Extensions"
            "System.Runtime.Numerics"
            "Microsoft.Quantum.QSharp.Core"
            "Microsoft.Quantum.Runtime.Core"
            "Microsoft.Quantum.Simulation.Common"
            "Microsoft.Quantum.Simulation.Simulators"
        ]
        |> List.map (fun name -> upcast MetadataReference.CreateFromFile (referencedAssembly name))

    let syntaxTrees = sources |> Seq.map CSharpSyntaxTree.ParseText
    let compilation = CSharpCompilation.Create ("GeneratedEntryPoint", syntaxTrees, references)
    use stream = new MemoryStream ()
    let result = compilation.Emit stream
    Assert.True (result.Success, String.Join ("\n", result.Diagnostics))
    Assert.Equal (0L, stream.Seek (0L, SeekOrigin.Begin))
    Assembly.Load (stream.ToArray ())

/// The assembly for the given test case.
let private testAssembly = generateCsharp >> compileCsharp

/// Runs the entry point driver in the assembly with the given command-line arguments, and returns the output.
let private run (assembly : Assembly) (args : string[]) =
    let driver = assembly.GetType (EntryPoint.generatedNamespace testNamespace + ".Driver")
    let main = driver.GetMethod("Main", BindingFlags.NonPublic ||| BindingFlags.Static)
    let previousCulture = CultureInfo.DefaultThreadCurrentCulture
    let previousOut = Console.Out

    CultureInfo.DefaultThreadCurrentCulture <- CultureInfo ("en-US", false)
    use stream = new StringWriter ()
    Console.SetOut stream
    let exitCode = main.Invoke (null, [| args |]) :?> Task<int> |> Async.AwaitTask |> Async.RunSynchronously
    Console.SetOut previousOut
    CultureInfo.DefaultThreadCurrentCulture <- previousCulture

    stream.ToString (), exitCode

/// Asserts that running the entry point in the assembly with the given arguments succeeds and yields the expected
/// output.
let private yields expected (assembly, args) =
    let output, exitCode = run assembly args
    Assert.Equal (0, exitCode)
    Assert.Equal (expected, output.TrimEnd ())

/// Asserts that running the entry point in the assembly with the given arguments fails.
let private fails (assembly, args) =
    let output, exitCode = run assembly args
    Assert.True (0 <> exitCode, "Succeeded unexpectedly:" + Environment.NewLine + output)

/// A tuple of the test assembly for the given test number, and the given argument string converted into an array.
let test testNum =
    let assembly = testAssembly testNum
    fun args -> assembly, Array.ofList args

// No Option

[<Fact>]
let ``Returns Unit`` () =
    let given = test 1
    given [] |> yields ""
    
[<Fact>]
let ``Returns Int`` () =
    let given = test 2
    given [] |> yields "42"

[<Fact>]
let ``Returns String`` () =
    let given = test 3
    given [] |> yields "Hello, World!"


// Single Option

[<Fact>]
let ``Accepts Int`` () =
    let given = test 4
    given ["-n"; "42"] |> yields "42"
    given ["-n"; "4.2"] |> fails
    given ["-n"; "9223372036854775807"] |> yields "9223372036854775807"
    given ["-n"; "9223372036854775808"] |> fails
    given ["-n"; "foo"] |> fails

[<Fact>]
let ``Accepts BigInt`` () =
    let given = test 5
    given ["-n"; "42"] |> yields "42"
    given ["-n"; "4.2"] |> fails
    given ["-n"; "9223372036854775807"] |> yields "9223372036854775807"
    given ["-n"; "9223372036854775808"] |> yields "9223372036854775808"
    given ["-n"; "foo"] |> fails

[<Fact>]
let ``Accepts Double`` () =
    let given = test 6
    given ["-n"; "4.2"] |> yields "4.2"
    given ["-n"; "foo"] |> fails

[<Fact>]
let ``Accepts Bool`` () =
    let given = test 7
    given ["-b"] |> yields "True"
    given ["-b"; "false"] |> yields "False"
    given ["-b"; "true"] |> yields "True"
    given ["-b"; "one"] |> fails

[<Fact>]
let ``Accepts Pauli`` () =
    let given = test 8
    given ["-p"; "PauliI"] |> yields "PauliI"
    given ["-p"; "PauliX"] |> yields "PauliX"
    given ["-p"; "PauliY"] |> yields "PauliY"
    given ["-p"; "PauliZ"] |> yields "PauliZ"
    given ["-p"; "PauliW"] |> fails

[<Fact>]
let ``Accepts Result`` () =
    let given = test 9
    given ["-r"; "Zero"] |> yields "Zero"
    given ["-r"; "zero"] |> yields "Zero"
    given ["-r"; "One"] |> yields "One"
    given ["-r"; "one"] |> yields "One"
    given ["-r"; "0"] |> yields "Zero"
    given ["-r"; "1"] |> yields "One"
    given ["-r"; "Two"] |> fails

[<Fact>]
let ``Accepts Range`` () =
    let given = test 10
    given ["-r"; "0..0"] |> yields "0..1..0"
    given ["-r"; "0..1"] |> yields "0..1..1"
    given ["-r"; "0..2..10"] |> yields "0..2..10"
    given ["-r"; "0"; "..1"] |> yields "0..1..1"
    given ["-r"; "0.."; "1"] |> yields "0..1..1"
    given ["-r"; "0"; ".."; "1"] |> yields "0..1..1"
    given ["-r"; "0"; "..2"; "..10"] |> yields "0..2..10"
    given ["-r"; "0.."; "2"; "..10"] |> yields "0..2..10"
    given ["-r"; "0"; ".."; "2"; ".."; "10"] |> yields "0..2..10"
    given ["-r"; "0"; "1"] |> yields "0..1..1"
    given ["-r"; "0"; "2"; "10"] |> yields "0..2..10"
    given ["-r"; "0"] |> fails
    given ["-r"; "0.."] |> fails
    given ["-r"; "0..2.."] |> fails
    given ["-r"; "0..2..3.."] |> fails
    given ["-r"; "0..2..3..4"] |> fails
    given ["-r"; "0"; "1"; "2"; "3"] |> fails

[<Fact>]
let ``Accepts String`` () =
    let given = test 11
    given ["-s"; "Hello, World!"] |> yields "Hello, World!"

[<Fact>]
let ``Accepts String array`` () =
    let given = test 12
    given ["--xs"; "foo"] |> yields "[foo]"
    given ["--xs"; "foo"; "bar"] |> yields "[foo,bar]"
    given ["--xs"; "foo bar"; "baz"] |> yields "[foo bar,baz]"
    given ["--xs"; "foo"; "bar"; "baz"] |> yields "[foo,bar,baz]"

[<Fact>]
let ``Accepts Unit`` () =
    let given = test 13
    given ["-u"; "()"] |> yields ""
    given ["-u"; "42"] |> fails