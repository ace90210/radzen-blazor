using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Radzen;
using Radzen.Blazor;
using RadzenBlazorDemos;
using RadzenBlazorDemos.Data;
using RadzenBlazorDemos.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents().AddHubOptions(o =>
    {
        o.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    });
builder.Services.AddSingleton(sp =>
{
    // Get the address that the app is currently running at
    var server = sp.GetRequiredService<IServer>();
    var addressFeature = server.Features.Get<IServerAddressesFeature>();
    string baseAddress = addressFeature != null ? addressFeature.Addresses.First() : string.Empty;
    return new HttpClient { BaseAddress = new Uri(baseAddress) };
});

// Add Radzen.Blazor services
builder.Services.AddRadzenComponents();
builder.Services.AddRadzenQueryStringThemeService();

// Demo services
builder.Services.AddScoped<CompilerService>();
builder.Services.AddScoped<ExampleService>();

builder.Services.AddDbContextFactory<NorthwindContext>();

builder.Services.AddScoped<NorthwindService>();
builder.Services.AddScoped<NorthwindODataService>();
builder.Services.AddSingleton<GitHubService>();
builder.Services.AddSingleton<ZipBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ZipBackgroundService>());

builder.Services.AddAIChatService(options =>
    builder.Configuration.GetSection("AIChatService").Bind(options));


builder.Services.AddLocalization();

/* --> Uncomment to enable localization
var supportedCultures = new[]
{
    new System.Globalization.CultureInfo("de-DE"),
};

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("de-DE");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});
*/

var app = builder.Build();
// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

/* --> Uncomment to enable localization
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture("de-DE"),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedCultures
});
*/
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();
app.MapRazorPages();
app.MapRazorComponents<RadzenBlazorDemos.Server.App>()
    .AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(RadzenBlazorDemos.Routes).Assembly);
app.MapControllers();

app.MapGet("/api/download/{token}", async (string token, FileManagerService fileService, HttpContext context) =>
{
    if (fileService.TryGetToken(token, out var entry))
    {
        // 1. Determine the Filename
        // Use the override if provided (e.g., "Archive.zip"), otherwise get from disk (e.g., "image.jpg")
        var finalFileName = !string.IsNullOrEmpty(entry.DownloadName)
            ? entry.DownloadName
            : Path.GetFileName(entry.Path);

        // 2. Set Content-Disposition (Forces browser to download with specific name)
        context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{finalFileName}\"");

        if (entry.IsZip)
        {
            // ... (Your existing Zip Logic - kept short for brevity) ...
            var syncIOFeature = context.Features.Get<IHttpBodyControlFeature>();
            if (syncIOFeature != null) syncIOFeature.AllowSynchronousIO = true;

            context.Response.ContentType = "application/zip";
            await fileService.DownloadZipToStreamAsync(entry.SourcePaths, context.Response.Body);
        }
        else
        {
            // 3. SINGLE FILE DOWNLOAD LOGIC

            // This tells ASP.NET: "When the response is finished sending to the user, run this code."
            if (entry.DeleteAfter)
            {
                context.Response.OnCompleted(() =>
                {
                    try
                    {
                        if (File.Exists(entry.Path)) File.Delete(entry.Path);
                    }
                    catch { /* Log error safely */ }
                    return Task.CompletedTask;
                });
            }

            // Stream the file
            var fileInfo = new System.IO.FileInfo(entry.Path);
            return Results.File(entry.Path, "application/octet-stream", finalFileName);
        }

        return Results.Empty;
    }
    return Results.NotFound("Download link expired or invalid.");
});

app.Run();