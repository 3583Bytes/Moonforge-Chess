using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ChessBin.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// The Cloudflare Worker that holds state the static site cannot: community votes today.
// A separate client because it has a different base address; the one above serves files
// from the site itself. The URL comes from wwwroot/appsettings.json, overridden in
// development by appsettings.Development.json.
string apiBaseUrl = builder.Configuration["Api:BaseUrl"] ?? "https://chessbin-api.3583bytes.workers.dev";
if (!apiBaseUrl.EndsWith('/')) apiBaseUrl += "/";   // else a relative path replaces the last segment

builder.Services.AddScoped<IVoteApi>(_ => new HttpVoteApi(new HttpClient { BaseAddress = new Uri(apiBaseUrl) }));

// The opening explorer reads committed static JSON from the site itself, so it takes the
// site's own client rather than the API one. Scoped, so the shards it fetches stay cached
// for the session instead of being re-downloaded on every navigation.
builder.Services.AddScoped(sp => new OpeningExplorer(sp.GetRequiredService<HttpClient>()));

// Online games go through the same Worker as the votes, so they share its base address. The
// Worker owns seats, turn order and clocks; the rules stay here, in the browser — see
// ChessBin.Web/OnlineSession.cs.
builder.Services.AddScoped<IPlayApi>(_ => new HttpPlayApi(new HttpClient { BaseAddress = new Uri(apiBaseUrl) }));

await builder.Build().RunAsync();
