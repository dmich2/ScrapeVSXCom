module ScrapeVSXCom

open Prelude
open FSharp.Data
open System.Text.RegularExpressions

type Category =
  { Name : string
    Url  : string }

type ArticleType = 
  | HowTo
  | Bug
  | Other
with 
  override this.ToString() = sprintf "%A" this

type Article =
  { Title      : string
    Url        : string
    Type       : ArticleType
    Categories : Category[]
    Date       : System.DateTime }

type ArchiveMonth =
  { Date      : System.DateTime
    Url       : string
    NArticles : int }

(* Grabbing functions *)

let convertUrlToFilename (url:string) =
  Some (Http.convertUrlToLocalFilename url)

let config = { Http.CacheFilenameF = convertUrlToFilename }

let grabPage url = 
  Http.downloadFromURL config Http.UseCache url

let vsxUrl = "http://www.visualstudioextensibility.com"
let absVsxUrl = Http.absoluteUrl vsxUrl

let articleTypeRE = 
  Regex("""(?ix)^
            (?:
             (?<category> MZ-?Tools \s+ Articles \s+ Series (?: \s+ \(Updated?\))? ) \s* :? \s* 
             (?: (?<type> HOWTO) \s+ | (?<type> [^:]+) : )?
             (?<title> .*) 
            |
             (?<type> HOWTO | BUG:) \s+ (?<title> .*) 
            ) $""",
        RegexOptions.Compiled)

let getArticleType fullTitle =
  let convertArticleType = function
    | "howto" -> HowTo
    | "bug"   -> Bug
    | _       -> Other

  match fullTitle with
  | General.RegexGroups articleTypeRE groups -> 
    if groups.ContainsKey "type" then
      let typeStr = groups.["type"].ToLower()
      fullTitle, (convertArticleType typeStr)
    else
      fullTitle, Other
  | _ -> fullTitle, Other
  
let parseTitle (articleNode:HtmlNode) =
  Option.maybe {
    let! header = articleNode.First "h1" "entry-title"
    let! aref = header.First Html.Aref ""
    let url = absVsxUrl (aref.AttributeValue Html.Href)
    let fullTitle = aref.InnerText().Trim()
    let title, aType = getArticleType fullTitle
    return title, url, aType
    }

let convertDate s =
  match System.DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.None) with
  | true, date -> date
  | _ -> System.DateTime.MinValue

let parseDate (articleNode:HtmlNode) =
  Option.maybe {
    let! span = articleNode.First Html.Span "entry-date"
    let! timeElem = span.First "time" "entry-date"
    let dateStr = timeElem.AttributeValue "datetime"
    return convertDate dateStr
    }

let parseCategories (articleNode:HtmlNode) =
  Option.maybe {
    let! span = articleNode.First Html.Span "cat-links"
    let categories =
      span.Descendants("a", false)
      |> Seq.map (fun aref ->
        let url = absVsxUrl (aref.AttributeValue Html.Href)
        let name = aref.InnerText().Trim()
        { Name = name; Url = url }
        )
      |> Array.ofSeq
    return categories
    }
  
let parseArticle articleNode =
  let titleInfo  = articleNode |> parseTitle
  let date       = articleNode |> parseDate
  let categories = articleNode |> parseCategories
  match titleInfo, date, categories with
  | Some titleInfo, Some date, Some categories ->
    let title, url, aType = titleInfo
    Some { Title = title; Url = url; Type = aType; Categories = categories; Date = date}
  | Some titleInfo, Some date, _ -> 
    let title, url, aType = titleInfo
    Some { Title = title; Url = url; Type = aType; Categories = [| |]; Date = date}
  | _ -> None

let getNextUrl (doc:HtmlDocument) =
  Option.maybe {
  let! div = doc.First Html.Div "pagination loop-pagination"
  let! aref = div.First Html.Aref "next page-numbers"
  return absVsxUrl (aref.AttributeValue Html.Href)
  }

let scrapeDoc doc =
  doc
  |> HtmlDocument.descendants false (fun n -> n.HasName "article")
  |> Seq.choose parseArticle
 
let getPageDoc url =
  let data = grabPage url
  FSharp.Data.HtmlDocument.Parse data

let grabMonth month =
  let rec loop xs url =
    let doc = getPageDoc url
    let articles = Seq.append xs (scrapeDoc doc)
    match getNextUrl doc with
    | Some url -> loop articles url
    | _ -> articles
  
  let articles = 
    loop [] month.Url
    |> Array.ofSeq
  if articles.Length <> month.NArticles then
    Logging.logfn "*** Warning: %s #%d articles doesn't match expected #%d" 
      (month.Date.ToString("yyyy/MM")) articles.Length month.NArticles
  articles

let nArticlesRE = Regex(@"(?x) \( \s* (?<nArticles> \d+ ) \s* \) $", RegexOptions.Compiled)
let monthYearRE = Regex(@"(?x) / (?<year> \d+ )  / (?<month> \d+ ) /? $", RegexOptions.Compiled)

let grabMonths () =
  let getArchiveMonth (item:HtmlNode) =
    let url = 
      Option.maybe {
        let! aref = item.First Html.Aref ""
        return absVsxUrl (aref.AttributeValue Html.Href)
      }
    match url with
    | Some url -> 
      let nArticles = 
        match item.InnerText().Trim() with
        | General.RegexGroups nArticlesRE groups -> groups.["nArticles"] |> int
        | _ -> 0
      let date = 
        match url with 
        | General.RegexGroups monthYearRE groups ->
          let year = groups.["year"] |> int
          let month = groups.["month"] |> int
          System.DateTime(year, month, 1)
        | _ -> System.DateTime.MinValue
      Some { Date = date; Url = url; NArticles = nArticles }
    | _ -> None

  let doc = getPageDoc vsxUrl
  let months =
    Option.maybe {
      let! archives = doc.First "aside" "widget widget_archive"
      let items = archives.Descendants "li"
      let archiveMonths = 
        items
        |> Seq.choose getArchiveMonth
      return archiveMonths
    }
  match months with
  | Some m -> m |> Array.ofSeq
  | _ -> [| |]

(* Reporting functions *)

let dumpDateSummary (years:(int * seq<Article>)[]) =
  Logging.logfn "%s (%s):" "VisualStudioExtensibility Articles By Year" (Reporting.addCommasToInt years.Length)
  Logging.logfn ""

  let totalTuple = 
    (0, years)
    ||> Seq.fold (fun acc (year, items) ->
        let nItems = items |> Seq.length
        Logging.logfn "  %6s %d" (Reporting.addCommasToInt nItems) year
        acc + nItems)

  Logging.logfn "  ------"
  Logging.logfn "  %6s" (Reporting.addCommasToInt totalTuple)
  Logging.logfn ""

let reportArticles (articles:Article[]) =
  let dumpArticleLine (article:Article) = 
    let date = article.Date.ToString("yyyy-MM-dd HH:mm")
    Logging.logfnne "  %s  %-5O  %s" date article.Type article.Title

  let dumpGrouped msg (groups:(string * seq<Article>)[]) =
    Logging.logfn "%s (%s):" msg (Reporting.addCommasToInt groups.Length)

    let total =
      (0, groups)
      ||> Seq.fold (fun acc (group, articles) ->
          let nArticles = articles |> Seq.length
          Logging.logfn ""
          Logging.logfn " %s (%d):" group nArticles
          for article in articles do dumpArticleLine article
          acc + nArticles)
    
//    Logging.logfn "  ------"
//    Logging.logfn "  %6s" (Reporting.addCommasToInt total)
    Logging.logfn ""
    Logging.logfn "%s" (String.replicate 72 "=")
    Logging.logfn ""

  let groupAndDump msg reverse f =
    let groups = articles |> Reporting.categorize f |> Array.ofSeq
    let groups = if reverse then groups |> Array.rev else groups
    dumpGrouped msg groups

  let dumpSorted msg f =
    let sorted = articles |> Array.sortBy f
    Logging.logfn "%s (%s):" msg (Reporting.addCommasToInt sorted.Length)
    Logging.logfn ""
    for article in sorted do dumpArticleLine article
    Logging.logfn ""
    Logging.logfn "%s" (String.replicate 72 "=")
    Logging.logfn ""

  Logging.logfn ""
  Logging.logfn "# of articles: %s" (Reporting.addCommasToInt articles.Length)
  Logging.logfn ""

  let withCategories =
    seq { for article in articles do
            for category in article.Categories do
              yield category, article }
    |> Seq.groupBy (fun (category, _) -> category.Name)
    |> Array.ofSeq

  withCategories |> Reporting.dumpCategorySummary "VisualStudioExtensibility Articles By Category" string

  let byYear = articles |> Reporting.categorize (fun v -> v.Date.Year) |> Array.rev
  dumpDateSummary byYear

  articles |> Reporting.categorize (fun a -> a.Type)
           |> Reporting.dumpCategorySummary "VisualStudioExtensibility Articles By Type" string

  let articlesWithCategories =
    withCategories |> Array.map (fun (c, pairs) -> 
                        let articles = pairs |> Seq.map (fun (_, articles) -> articles)
                        c, articles )
  articlesWithCategories |> dumpGrouped "VisualStudioExtensibility Articles By Category"

  groupAndDump "VisualStudioExtensibility Articles By Type" false (fun a -> a.Type.ToString())
  groupAndDump "VisualStudioExtensibility Articles By Month" true (fun a -> a.Date.ToString("yyyy/MM"))
  dumpSorted "VisualStudioExtensibility Articles By title" (fun a -> a.Title)
  
  withCategories

let writeTSVFile (categorizedArticles:(string * seq<Category * Article>)[]) =
  use sw = new System.IO.StreamWriter("ScrapeVSXCom.csv", false, System.Text.Encoding.UTF8)
  sw.WriteLine("Category\tDate\tTitle\tType\tURL\tCategoryURL")
  let mutable count = 0
  for categoryName, articles in categorizedArticles do
    for category, article in articles do
      let line = sprintf "%s\t%s\t%s\t%s\t%s\t%s"
                   categoryName
                   (article.Date.ToString("MM/dd/yyyy HH:mm"))
                   article.Title
                   (article.Type.ToString())
                   article.Url
                   category.Url
      sw.WriteLine line
      count <- count + 1
  Logging.logfn "Created ScrapeVSXCom.csv with %d entries" count
  Logging.logfn ""

let scrapeVSXCom () =
  grabMonths()
  |> Array.collect grabMonth
  |> reportArticles
  |> writeTSVFile

#if COMPILED
[<EntryPoint>]
let main argv = 
  System.IO.Directory.SetCurrentDirectory(__SOURCE_DIRECTORY__)
  match argv with
  | [| |] -> Logging.wrapOutput scrapeVSXCom
  | _ -> printfn "Usage: ScrapeVSXCom"
  0 // return an integer exit code
#endif
