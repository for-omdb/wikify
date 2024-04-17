using System.Globalization;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Io;
using CsvHelper;

const string CsvPath = "../../../../Inputs/wikipedia-movie-list-short.csv";
const string OutFolder = "../../../../Outputs/";
const int PauseDurationMs = 6000;
// See: https://foundation.wikimedia.org/wiki/Policy:User-Agent_policy
const string UserAgent = "OmdbScanner/1.0 (test@test.com) dotNet/8.0";//TODO: Update this.

const string wikiBase = "https://en.wikipedia.org";
const string ImageAnchorSelector = ".infobox-image a";
const string FullImageSelector = ".fullImageLink a";
const string AuthorSelector = "#fileinfotpl_aut + td, .fileinfotpl_src + td a, #fileinfotpl_src + td p a:not(.autonumber), #fileinfotpl_src + td p, div.fileinfotpl_src a.text";
const string LicenseSelector = ".licensetpl_short";
const string LicenseContextSelector = ".licensetpl a[href='/wiki/Poster'], .licensetpl a[href='/wiki/Film_poster'], .licensetpl a[href='/wiki/DVD'], .licensetpl a[href='/wiki/Screenshot'], a[title='w:public domain'], a[title='en:public domain']";
const string UserSelector = ".wikitable.filehistory tr:nth-child(2) .mw-userlink";

async Task DoWorkAgainstUrl<T>(string url, T item, Func<IDocument, T, Task> work)
{
    var requester = new DefaultHttpRequester();
    requester.Headers["User-Agent"] = UserAgent;
    var config = Configuration.Default.With(requester).WithDefaultLoader();
    using var context = BrowsingContext.New(config);
    using var document = await context.OpenAsync(url);
    await work(document, item);
}

async Task StoreImage(string url, int movieId)
{
    var extension = Path.GetExtension(url);
    if (string.IsNullOrWhiteSpace(extension)) return;
    var destination = $"{OutFolder}{movieId}{extension}";
    using var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    var response = await client.GetByteArrayAsync(url);
    File.WriteAllBytes(destination, response);
}

async Task ProcessImagePage(IDocument document, int movieId)
{
    var imageAnchor = document.QuerySelector(FullImageSelector);
    var imageUrl = imageAnchor?.GetAttribute("href");
    if (imageUrl == null) return;
    if (imageUrl.StartsWith("//"))
    {
        imageUrl = "https:" + imageUrl;
    }

    await StoreImage(imageUrl, movieId);

    var author = document.QuerySelectorAll(AuthorSelector)?.MaxBy(x => x.Ancestors().Count())?.TextContent?.Trim();
    var license = document.QuerySelector(LicenseSelector)?.TextContent?.Trim();
    var license_context = document.QuerySelector(LicenseContextSelector)?.TextContent?.Trim();
    var user = document.QuerySelector(UserSelector)?.TextContent?.Trim();
    var options = new JsonSerializerOptions()
    {
        WriteIndented = true,
    };
    var combined = JsonSerializer.Serialize(new
    {
        author,
        user,
        license,
        license_context,
    }, options);
    File.WriteAllText($"{OutFolder}{movieId}.json", combined);
}

async Task ProcessMovie(string url, int movieId)
{
    await DoWorkAgainstUrl(url, movieId, async (document, _) =>
    {
        var imageAnchor = document.QuerySelector(ImageAnchorSelector);
        if (imageAnchor == null) return;
        var imageUrl = wikiBase + imageAnchor.GetAttribute("href");
        await DoWorkAgainstUrl(imageUrl, movieId, ProcessImagePage);
    });
}

async Task ProcessCsv(string path)
{
    using var reader = new StreamReader(path);
    using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
    var movies = csv.GetRecords<Movie>().ToArray();
    foreach (var movie in movies)
    {
        var expectedPath = $"{OutFolder}{movie.movie_id}.json";
        var skipPath = $"{OutFolder}{movie.movie_id}.skip.txt";
        try
        {
            if (File.Exists(expectedPath) || File.Exists(skipPath))
            {
                Console.WriteLine($"Skipped - {movie.movie_id}: {movie.link}");
                continue;
            }
            Console.WriteLine($"Processing - {movie.movie_id}: {movie.link}");
            await ProcessMovie(movie.link, movie.movie_id);
            var duration = (int)Math.Round(PauseDurationMs * (.5 + (new Random()).NextDouble() * .5));
            Thread.Sleep(duration);
            if (!File.Exists(expectedPath))
            {
                Console.WriteLine($"Issue - {movie.movie_id}: {movie.link}");
                File.WriteAllText(skipPath,
                    $"Seems like there was a missing image or other issue at this page: {movie.link}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error - {movie.movie_id}: {movie.link}");
            File.WriteAllText(skipPath, "Error: " + ex.Message);
        }
    }
}

Console.WriteLine("Started...");

await ProcessCsv(CsvPath);

Console.WriteLine("Done!");