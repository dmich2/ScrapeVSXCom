module Prelude.Http

//open Chessie.ErrorHandling

let absoluteUrl baseUrl (relativeUrl:string) =
  System.Uri(System.Uri(baseUrl), relativeUrl).AbsoluteUri

let convertUrlToLocalFilename (url:string) =
  let uri = System.Uri(url)
  let local = uri.LocalPath.ToString()
  let local = if local.EndsWith "/" then local + "index.html" else local
  let local = if local.StartsWith "/" then local else "/" + local
  let s = sprintf "%s%s" (uri.Host.ToString()) local
  let f = 
    match System.IO.Path.GetExtension s with
    | "" -> 
        (System.IO.Path.GetDirectoryName s) + @"\" +
        (System.IO.Path.GetFileNameWithoutExtension s) + ".html"
    | _ -> s
  "cache/" + f.Replace(@"\", "/")

type HttpConfiguration = 
  { CacheFilenameF : string -> string option
    //LoggingF : (Printf.StringFormat<'a,unit> -> 'a) 
  }

let cc = System.Net.CookieContainer()

let downloadBinaryFromUrl url =
  Logging.logfn "Downloading: %s" url
  let response = 
    FSharp.Data.Http.Request(url, silentHttpErrors=true,
                             headers = [FSharp.Data.HttpRequestHeaders.UserAgent 
                             "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:40.0) Gecko/20100101 Firefox/40.0"],
                             cookieContainer = cc)
  match response.StatusCode with
  | 200 -> 
    match response.Body with
    | FSharp.Data.Binary bytes -> Some bytes
    | _ -> None
  | status -> 
    Logging.logfn "%d %A: %s" status (enum<System.Net.HttpStatusCode>(status)) (System.IO.Path.GetFileName url)
    None

let private downloadLow filename url =
  let createMissingDirectory f =
    let directory = System.IO.Path.GetDirectoryName f
    if not (System.String.IsNullOrWhiteSpace directory) && 
       not (System.IO.Directory.Exists directory) then
      System.IO.Directory.CreateDirectory directory |> ignore

  Logging.logfn "Downloading: %s" url
  let response = 
    FSharp.Data.Http.Request(url, silentHttpErrors=true,
                             headers = [FSharp.Data.HttpRequestHeaders.UserAgent 
                             "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:40.0) Gecko/20100101 Firefox/40.0"],
                             cookieContainer = cc)
  match response.StatusCode with
  | 200 -> 
    match response.Body with
    | FSharp.Data.Text s ->
      match filename with
      | Some f -> 
        createMissingDirectory f
        System.IO.File.WriteAllText(f, s, System.Text.Encoding.UTF8)
      | _ -> ()
      Some response.Body
    | FSharp.Data.Binary b -> None
  | status -> 
    Logging.logfn "%d %A: %s" status (enum<System.Net.HttpStatusCode>(status)) url
    None

/// Skip cache even if cachefilename exists.
type UseCache = SkipCache | UseCache

let downloadFromURL config useCache url =
  let cacheFilename = config.CacheFilenameF url
  match cacheFilename with
  | Some filename -> 
    if useCache = UseCache && System.IO.File.Exists filename then
      Logging.logfn "Reading: %s" filename
      System.IO.File.ReadAllText(filename, System.Text.Encoding.UTF8)
    else
      match downloadLow cacheFilename url with
      | Some (FSharp.Data.Text s) -> s
      | _ -> ""
  | _ -> 
    match downloadLow cacheFilename url with
      | Some (FSharp.Data.Text s) -> s
      | _ -> ""

let noCacheConfig = { CacheFilenameF = fun _ -> Some "test.html" }
let getPageDocNoCache url =
  let data =  downloadFromURL noCacheConfig SkipCache url
  FSharp.Data.HtmlDocument.Parse data