using System.Text.Json;
using Wharf;
using Wharf.Api;
using Wharf.Storage;

var webRoot = WebRoot.Resolve();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = webRoot,
});

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    json.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

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
        "Wharf:Token is not set - the API is unauthenticated. Set Wharf__Token before exposing it.");

if (webRoot is null)
    app.Logger.LogWarning(
        "PWA web root not found. Set WHARF_WEB_ROOT or run from the repository root.");

if (!string.IsNullOrEmpty(options.DevOrigin))
    app.UseCors();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseWharfAuth(options);

app.MapHealthEndpoints();
app.MapManifestEndpoints();
app.MapEntryEndpoints();
app.MapUploadEndpoints();
app.MapDownloadEndpoints();
app.MapEventEndpoints();

app.Run();
