// Phase 0 skeleton: health endpoint only. Capture, storage and observer logic arrive in Phase 1+.
var builder = WebApplication.CreateSlimBuilder(args);

// Loopback only. Never bind to all interfaces.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:47613");

var app = builder.Build();

app.MapGet("/health", () => Results.Text("""{"status":"ok"}""", "application/json"));

app.Run();

public partial class Program;
