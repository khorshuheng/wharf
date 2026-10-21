using System.Text.Json;
using Wharf;
using Wharf.Api;
using Wharf.Components;
using Wharf.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    json.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

var options = WharfOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(options);

Directory.CreateDirectory(options.DataDirectory);

var database = new Database(Path.Combine(options.DataDirectory, "wharf.db"));
var blobs = new BlobStore(options.DataDirectory);
var index = new SyncIndex(database);
var uploads = new UploadStore(database);
var notifier = new ChangeNotifier();
var writer = new EntryWriter(index, notifier);

builder.Services.AddSingleton(database);
builder.Services.AddSingleton(index);
builder.Services.AddSingleton(blobs);
builder.Services.AddSingleton(uploads);
builder.Services.AddSingleton(notifier);
builder.Services.AddSingleton(writer);
builder.Services.AddHostedService<GcService>();

if (!string.IsNullOrEmpty(options.DevOrigin))
{
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
        policy.WithOrigins(options.DevOrigin).AllowAnyHeader().AllowAnyMethod()));
}

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");

var app = builder.Build();

if (!options.AuthEnabled)
    app.Logger.LogWarning(
        "Wharf:Token is not set - the API is unauthenticated. Use this only behind a trusted proxy or on loopback.");

if (app.Environment.IsDevelopment())
    app.UseWebAssemblyDebugging();

if (!string.IsNullOrEmpty(options.DevOrigin))
    app.UseCors();

app.UseStaticFiles();
app.UseAntiforgery();
app.UseWharfAuth(options);

app.MapStaticAssets();

// The service worker imports this list to precache the shell and framework
// assets. Generate it from the physical web root at request time: the
// fingerprinted names only exist after build/publish, and serving a file from
// wwwroot here would be shadowed by the build-time static asset manifest.
app.MapGet("/_service-worker-assets", (HttpContext context, IWebHostEnvironment environment) =>
{
    var webRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
    var assets = Directory.Exists(webRoot)
        ? Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            .Where(file => !Path.GetFileName(file).Equals("service-worker-assets.js", StringComparison.OrdinalIgnoreCase))
            .Select(file => "/" + Path.GetRelativePath(webRoot, file).Replace('\\', '/'))
            .OrderBy(url => url, StringComparer.Ordinal)
            .ToArray()
        : [];

    var json = string.Join(",", assets.Select(url => $"\"{url}\""));
    context.Response.Headers.CacheControl = "no-cache";
    return Results.Text($"self.wharfAssets = [{json}];", "text/javascript");
});

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Wharf.Client._Imports).Assembly);

app.MapHealthEndpoints();
app.MapManifestEndpoints();
app.MapEntryEndpoints();
app.MapUploadEndpoints();
app.MapDownloadEndpoints();
app.MapEventEndpoints();

app.Run();
