namespace Prelude

[<AutoOpen>]
module CommonOperators =
  let (</>) x y = System.IO.Path.Combine(x, y)
  let inline isNull x = match x with null -> true | _ -> false

[<AutoOpen>]
module Dict =
  open System.Collections.Generic
  //let toSeq d = d |> Seq.map (fun (KeyValue(k,v)) -> (k,v))
  let containsKey k (d:IDictionary<_,_>) = d.ContainsKey k

[<AutoOpen>]
module Option = 
  let getOrElse v = function Some x -> x | _ -> v
  let applyOrElse v f = function Some x -> f x | _ -> v

  type MaybeBuilder() =
      member this.Bind(x, f) = Option.bind f x
      member this.Return(x)  = Some x
  let maybe = new MaybeBuilder()

[<AutoOpen>]
module Seq = let tryHead xs = if Seq.isEmpty xs then None else Some (Seq.head xs)

[<AutoOpen>]
module Map =
  let keys (m:Map<_,_>) = m |> Seq.map (fun (KeyValue(k, _)) -> k)
  let values (m:Map<_,_>) = m |> Seq.map (fun (KeyValue(_, v)) -> v)

[<AutoOpen>]
module Html =
  open FSharp.Data

  let Div = "div"
  let Aref = "a"
  let Image = "img"
  let Span = "span"
  let Href = "href"

  let grabNode name cssClass f x = 
    let nodes = x 
                |> f true (fun (n:HtmlNode) -> n.HasName name && (cssClass = "" || n.HasClass cssClass))
                |> List.ofSeq
    match nodes with
    | head :: tail -> Some head
    | _ -> None

  let optionallyExtractInnerText name cssClass defaultValue f x =
    (grabNode name cssClass f x |> Option.applyOrElse defaultValue HtmlNode.innerText).Trim()

  let getValue name cssClass defaultValue (n:HtmlNode) =
    optionallyExtractInnerText name cssClass defaultValue HtmlNode.descendants n
         
  let getValueFromDoc name cssClass defaultValue (d:HtmlDocument) =
    optionallyExtractInnerText name cssClass defaultValue HtmlDocument.descendants d

  type FSharp.Data.HtmlDocument with
    member x.First name cssClass =
      grabNode name cssClass HtmlDocument.descendants x
    member x.GetValue name cssClass defaultValue =
      getValueFromDoc name cssClass defaultValue x
  
  type FSharp.Data.HtmlNode with
    member x.First name cssClass =
      grabNode name cssClass HtmlNode.descendants x
    member x.GetValue name cssClass defaultValue =
      getValue name cssClass defaultValue x

module General =
  open System.Text.RegularExpressions
  type Directory = System.IO.Directory
  type SearchOption = System.IO.SearchOption
  type File = System.IO.File
  type Path = System.IO.Path

  let (|NotEmptyArray|_|) (a: 'a []) = 
    match a with
    | null | [||] -> None
    | _ -> Some a

  let (|RegexGroups|_|) (re:Regex) input =
    let m = re.Match input
    if m.Success then 
      [for g in re.GetGroupNames() do
        if m.Groups.[g].Success then
          yield (g, m.Groups.[g].Value.Trim()) ]
      |> Map.ofSeq
      |> Some
    else None

  let skipIf n s =
    s
    |> Seq.mapi (fun i elem -> i, elem)
    |> Seq.choose (fun (i, elem) -> if i >= n then Some(elem) else None)

  /// Bump existing versioned files so filename.ext -> filename.1.ext, 
  ///  filename.1.ext -> filename.2.ext, etc. up to a max of 9 backups.
  /// Prevents overwriting of existing file by creating versioned backups.
  /// filename arg can be relative or absolute path and should NOT be of filename.1.ext form.
  /// Returns absolute filepath of filename.
  [<System.Diagnostics.CodeAnalysis.SuppressMessage("NumberOfItems", "MaxNumberOfItemsInTuple")>]
  let bumpFilenameVersions filename =
    let maxHistory = 9  // 9 "backups" + the current file

    let getFileParts filename =
      let filepath = Path.GetFullPath filename
      let directory = Path.GetDirectoryName filepath
      let basename = Path.GetFileNameWithoutExtension filepath
      let extension = Path.GetExtension filepath  // includes leading .
      let pattern = basename + "*" + extension
      filepath, directory, basename, extension, pattern

    /// Matches files like filename.1.txt
    let getVersionedFileRE basename extension =
      let pattern = sprintf @"(?ix) (^|\\) %s \. (?<version> \d+ ) %s $" basename (@"\" + extension)
      Regex(pattern, RegexOptions.Compiled)
    
    let getMatchingFilesByVersion (versionedfileRE:Regex) directory pattern =
      Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
      |> Array.filter versionedfileRE.IsMatch 
      |> Array.sortBy (function
        | RegexGroups versionedfileRE groups -> int groups.["version"]
        | _ -> 0 )
  
    let renameVersionedFile (versionedFileRE:Regex) 
                             directory basename extension = function
      | RegexGroups versionedFileRE groups as filename ->
        let version = int groups.["version"]
        let newBasename = sprintf "%s.%d%s" basename (version+1) extension
        File.Move(filename, directory </> newBasename)
      | _ -> ()

    let filepath, directory, basename, extension, pattern = getFileParts filename
    let versionedFileRE        = getVersionedFileRE basename extension
    let matchingVersionedFiles = getMatchingFilesByVersion versionedFileRE directory pattern

    match matchingVersionedFiles with
    | NotEmptyArray files ->
      files
      |> skipIf maxHistory
      |> Seq.iter System.IO.File.Delete

      files
      |> Seq.truncate maxHistory
      |> Array.ofSeq
      |> Array.rev
      |> Array.iter (renameVersionedFile versionedFileRE directory basename extension)
    | _ -> ()
    if System.IO.File.Exists filepath then
      File.Move (filepath, directory </> basename + ".1" + extension)

    filepath

  /// Delete all files in directory (but *not* subdirectories)
  let deleteFilesInDir path =
    if System.IO.Directory.Exists path then
      System.IO.Directory.GetFiles(path)
      |> Seq.iter System.IO.File.Delete

  /// Make sure directory exists
  let makeSureDirExists dir =
    if not (System.IO.Directory.Exists dir) then
      System.IO.Directory.CreateDirectory dir |> ignore

module Logging =
  let assembly = System.Reflection.Assembly.GetEntryAssembly()
  let location = if isNull assembly then
                   System.IO.Directory.GetCurrentDirectory() 
                 else
                   assembly.Location
  let name = System.IO.Path.GetFileNameWithoutExtension location
  let logfilename = General.bumpFilenameVersions (name + ".log")
  let logfile     = new System.IO.StreamWriter(logfilename, false, System.Text.Encoding.UTF8)

  let logfne echo format =
    Printf.kprintf (fun s -> 
      System.IO.TextWriter.Synchronized(logfile).WriteLine s
      if echo then System.Console.WriteLine s) format
  let logfn format = logfne true format
  let logfnne format = logfne false format

  let logf format =
    Printf.kprintf (fun s -> 
      System.IO.TextWriter.Synchronized(logfile).Write s
      System.Console.Write s) format

  let loggerClose() =
    logfile.Close()

  let wrapOutput f =
    let startTime = System.DateTime.Now
    f()
    let now = System.DateTime.Now 
    let elapsedTime = now - startTime
    logfn "%s, Elapsed time: %s" (now.ToString("yyyy\/MM\/dd HH\:mm\:ss ddd")) 
                                (elapsedTime.ToString("mm\:ss\.ffff"))
    loggerClose()

module Reporting =
  let dateFormat = "yyyy-MM-dd"

  let addCommasToInt (i : int) = System.String.Format("{0:n0}", i)
  let addCommasToUint64 (i : uint64) = System.String.Format("{0:n0}", i)

  let dumpItems heading headerf f (items:'a[]) =
    Logging.logfn "%s (%s):" heading (addCommasToInt items.Length)
    Logging.logfn ""
    headerf
    items |> Array.iter f

  let categorize f = Seq.groupBy f >> Seq.sortBy fst >> Array.ofSeq

  let dumpSummary heading f (categories:('a * #seq<'b>)[]) =
    Logging.logfn "%s (%s):" heading (addCommasToInt categories.Length)
    Logging.logfn ""

    let total =
      (0, categories)
      ||> Seq.fold (fun acc (category, items) ->
          let nItems = items |> Seq.length
          f category nItems items
          acc + nItems)
    Logging.logfn " ------"
    Logging.logfn " %6s" (addCommasToInt total)
    Logging.logfn ""

  let dumpCategorySummary heading f (categories:('a * #seq<'b>)[]) =
    dumpSummary heading (fun category nItems _ ->
        let nItemsStr = addCommasToInt nItems
        Logging.logfn " %6s %s" nItemsStr (f category)
      ) categories

  let dumpDateSummary (categories:(System.DateTime * #seq<'b>)[]) =
    dumpCategorySummary "Date Summary" (fun (d:System.DateTime) -> d.ToString(dateFormat)) categories