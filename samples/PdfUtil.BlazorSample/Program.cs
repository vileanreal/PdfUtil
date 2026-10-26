using PdfUtil.BlazorSample;
using PdfUtil.BlazorSample.Components;
using PdfUtil.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Registers every PdfUtil service as a singleton. Library log messages go to ILogger (category "PdfUtil") and never
// contain passwords.
builder.Services.AddPdfUtil(options => options.MaxFileSizeBytes = SampleLimits.MaxUploadBytes);

// Signing and certificate encryption (PdfUtil.Signatures), and conversion between PDF, HTML and Word
// (PdfUtil.Conversion). Conversion never fetches anything from the network.
builder.Services.AddPdfUtilSignatures();
builder.Services.AddPdfUtilConversion(limits => limits.MaxInputBytes = SampleLimits.MaxUploadBytes);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
