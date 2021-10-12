namespace Myriad.Core

open System
open FSharp.Compiler
open FSharp.Compiler.ErrorLogger
open FSharp.Compiler.Range
open FSharp.Compiler.XmlDoc
open Fantomas
open FSharp.Compiler.SyntaxTree
open FSharp.Compiler.SourceCodeServices

module Ast =
    let fromFilename filename =
        let allLines = Generation.linesToKeep filename |> String.concat Environment.NewLine
        let parsingOpts = {FSharpParsingOptions.Default with
                               SourceFiles = [| filename |]
                               ErrorSeverityOptions = FSharpErrorSeverityOptions.Default }
        let checker = FSharpChecker.Create()
        CodeFormatter.ParseAsync(filename, SourceOrigin.SourceString allLines, parsingOpts, checker)

    let typeNameMatches (attributeType: Type) (attrib: SynAttribute) =
        match attrib.TypeName with
        | LongIdentWithDots(ident, _range) ->
            let ident =
                ident
                |> List.map(fun id -> id.ToString())
                |> String.concat(".")
                |> function s -> if s.EndsWith "Attribute" then s else s + "Attribute"
            //Support both full name and opened namespace - this is not 100% accurate, and it can't be without full type information, but it should be good enough in practice
            //Replace `+` with `.` - `+` is naming convention for embeded types - in our case it may be an attribute type defined inside module.
            if ident.Contains "." then
                attributeType.FullName.Replace("+", ".").EndsWith(ident)
            else
                ident = attributeType.Name

    let getAttributeConstants (attrib: SynAttribute) =
        let (|StringConst|_|) = function
            | SynExpr.Const(SynConst.String(text,_), _) -> Some text
            | _ -> None

        match attrib.ArgExpr with
        | SynExpr.Paren(StringConst text,_,_,_) -> [text]
        | SynExpr.Paren(SynExpr.Tuple(_,entries,_,_),_,_,_) -> entries |> List.choose (|StringConst|_|)
        | StringConst text -> [text]
        | _ -> []

    let hasAttributeWithConst (attributeType: Type) (attributeArg: string) (attrib: SynAttribute) =

        let argumentMatched attributeArg =
            match getAttributeConstants attrib with
            | [] -> false
            | t -> t  |> List.contains attributeArg

        typeNameMatches attributeType attrib && (argumentMatched attributeArg)

    let (|HasAttribute|_|) (attributeName: string) (attributes: SynAttributes) =
        attributes
        |> List.collect (fun n -> n.Attributes)
        |> List.tryFind (hasAttributeWithConst typeof<MyriadGeneratorAttribute> attributeName)

    let hasAttributeWithName<'a> (attributeName: string) (TypeDefn(ComponentInfo(attributes, _typeParams, _constraints, _recordIdent, _doc, _preferPostfix, _access, _), _typeDefRepr, _memberDefs, _))  =
        attributes
        |> List.exists (fun n -> n.Attributes |> List.exists (hasAttributeWithConst typeof<'a> attributeName))

    let hasAttribute<'a>  (TypeDefn(ComponentInfo(attributes, _typeParams, _constraints, _recordIdent, _doc, _preferPostfix, _access, _), _typeDefRepr, _memberDefs, _))  =
        attributes
        |> List.collect (fun n -> n.Attributes)
        |> List.exists (typeNameMatches typeof<'a>)

    let getAttribute<'a>  (TypeDefn(ComponentInfo(attributes, _typeParams, _constraints, _recordIdent, _doc, _preferPostfix, _access, _), _typeDefRepr, _memberDefs, _))  =
        attributes
        |> List.collect (fun n -> n.Attributes)
        |> List.tryFind (typeNameMatches typeof<'a>)

    let extractTypeDefn (ast: ParsedInput) =
        let rec extractTypes (moduleDecls: SynModuleDecl list) (ns: LongIdent) =
            [   for moduleDecl in moduleDecls do
                    match moduleDecl with
                    | SynModuleDecl.Types(types, _) ->
                        yield (ns, types)
                    | SynModuleDecl.NestedModule(ComponentInfo(attribs, typeParams, constraints, longId, xmlDoc, preferPostfix, accessibility, range), isRec, decls, _, _range) ->
                        let combined = longId |> List.append ns
                        yield! (extractTypes decls combined)
                    | other -> ()
            ]

        [   match ast with
            | ParsedInput.ImplFile(ParsedImplFileInput(_name, _isScript, _qualifiedNameOfFile, _scopedPragmas, _hashDirectives, modules, _g)) ->
                for SynModuleOrNamespace(namespaceId, _isRec, _isModule, moduleDecls, _preXmlDoc, _attributes, _access, _range) as ns in modules do
                    yield! extractTypes moduleDecls namespaceId
            | _ -> () ]

    let isRecord (TypeDefn(_componentInfo, typeDefRepr, _memberDefs, _)) =
        match typeDefRepr with
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Record _, _) -> true
        | _ -> false

    let isDu (TypeDefn(_componentInfo, typeDefRepr, _memberDefs, _)) =
        match typeDefRepr with
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Union _, _) -> true
        | _ -> false

    let extractRecords (ast: ParsedInput) =
        let types = extractTypeDefn ast
        let onlyRecords =
            types
            |> List.map (fun (ns, types) -> ns, types |> List.filter isRecord )
        onlyRecords

    let extractDU (ast: ParsedInput) =
        let types = extractTypeDefn ast
        let onlyDus =
            types
            |> List.map (fun (ns, types) -> ns, types |> List.filter isDu )
        onlyDus
        
        
    module ModuleOrNamespace =
        let hasAttribute<'a>
            (SynModuleOrNamespace (_namespaceId, _isRec, _isModule, _moduleDecls, _preXmlDoc, attributes, _access, _range)) =
            attributes
            |> List.collect (fun n -> n.Attributes)
            |> List.exists (typeNameMatches typeof<'a>)
            
        let modulesWithAttribute<'a> (ast: ParsedInput) =
            [ match ast with
              | ParsedInput.ImplFile (ParsedImplFileInput (_name, _isScript, _qualifiedNameOfFile, _scopedPragmas, _hashDirectives, modules, _g)) ->
                  for SynModuleOrNamespace (_namespaceId, _isRec, moduleOrNs, _moduleDecls, _preXmlDoc, _attributes, _access, _range) as ns in modules do
                      if moduleOrNs.IsModule && hasAttribute<'a> ns then
                          yield ns
              | _ -> () ]
            
        let getTypeDefns (nsOrModule: SynModuleOrNamespace) =
            let rec extractTypes (moduleDecls: SynModuleDecl list) (ns: LongIdent) =
                [ for moduleDecl in moduleDecls do
                      match moduleDecl with
                      | SynModuleDecl.Types (types, _) -> yield (ns, types)
                      | SynModuleDecl.NestedModule (ComponentInfo (_attribs, _typeParams, _constraints, longId, _xmlDoc, _preferPostfix, _accessibility, _range), _isRec, decls, _local, _outerRange) ->
                          let combined = longId |> List.append ns
                          yield! (extractTypes decls combined)
                      | _other -> () ]

            let (SynModuleOrNamespace (namespaceId, _isRec, _isModule, moduleDecls, _preXmlDoc, _attributes, _access, _range)) =
                nsOrModule

            extractTypes moduleDecls namespaceId
        
        let records (nsOrModule: SynModuleOrNamespace) =
            let types = getTypeDefns nsOrModule

            let onlyRecords =
                types
                |> List.map (fun (ns, types) -> ns, types |> List.filter isRecord)

            onlyRecords

        let dus (nsOrModule: SynModuleOrNamespace) =
            let types = getTypeDefns nsOrModule

            let onlyDus =
                types
                |> List.map (fun (ns, types) -> ns, types |> List.filter isDu)

            onlyDus
        
        let recordsOrDus (nsOrModule: SynModuleOrNamespace) =
            let types = getTypeDefns nsOrModule

            let recordsOrDus =
                types
                |> List.map (fun (ns, types) -> ns, types |> List.filter (fun t -> t |> isDu || t |> isRecord))

            recordsOrDus
        
    open FsAst  
    module Ident =
        let asCamelCase (ident: Ident) =
            Ident.Create(ident.idText.Substring(0, 1).ToLowerInvariant() + ident.idText.Substring(1))
            
    type  ParsedImplFileInput with
        static member CreateFs(name, ?isScript, ?scopedPragmas, ?hashDirectives, ?modules, ?isLastCompiland, ?isExe) =
            let file = $"%s{name}.fs"
            let isScript = defaultArg isScript false
            let qualName = QualifiedNameOfFile.Create name
            let scopedPragmas = defaultArg scopedPragmas []
            let hashDirectives = defaultArg hashDirectives []
            let modules = defaultArg modules []
            let isLastCompiland = defaultArg isLastCompiland true
            let isExe = defaultArg isExe false
            ParsedImplFileInput(file, isScript, qualName, scopedPragmas, hashDirectives, modules, (isLastCompiland, isExe))
            
    type SynModuleOrNamespace with
        static member CreateNamespace(ident, ?isRecursive, ?decls, ?docs, ?attribs, ?access) =
            let range = range0
            let kind = SynModuleOrNamespaceKind.DeclaredNamespace
            let isRecursive = defaultArg isRecursive false
            let decls = defaultArg decls []
            let docs = defaultArg docs  PreXmlDoc.Empty
            let attribs = defaultArg attribs SynAttributes.Empty
            SynModuleOrNamespace(ident, isRecursive, kind, decls, docs, attribs, access, range)

