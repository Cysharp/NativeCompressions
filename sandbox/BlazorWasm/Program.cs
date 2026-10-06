using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BlazorWasm;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Run the native round trips before the UI starts, so the result is in the browser console even when the page fails.
foreach (var line in await WasmSmoke.RunAsync())
{
    Console.WriteLine(line);
}

await builder.Build().RunAsync();
