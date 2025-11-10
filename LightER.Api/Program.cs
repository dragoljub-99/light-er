using System.IO.Compression;
using System.Diagnostics;
using LightER.Analysis;
using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5112);
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();


app.MapGet("/api-health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/analyze", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart/form-data." });

    var form = await request.ReadFormAsync();
    if (form.Files.Count == 0)
        return Results.BadRequest(new { error = "No files provided. Upload .cs files or a .zip" });

    var tempRoot = Path.Combine(Path.GetTempPath(), "lighter", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);

    var csPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    try
    {
        foreach (var file in form.Files)
        {
            if (file.Length == 0)
                continue;

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (ext == ".cs")
            {
                var dest = Path.Combine(tempRoot, Path.GetFileName(file.FileName));
                using (var fs = File.Create(dest))
                    await file.CopyToAsync(fs);

                csPaths.Add(dest);
            }
            else if (ext == ".zip")
            {
                var zipPath = Path.Combine(tempRoot, Path.GetFileName(file.FileName));
                using (var fs = File.Create(zipPath))
                    await file.CopyToAsync(fs);

                var extractDir = Path.Combine(tempRoot, Path.GetFileNameWithoutExtension(file.FileName));
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                foreach (var p in Directory.EnumerateFiles(extractDir, "*.cs", SearchOption.AllDirectories))
                    csPaths.Add(p);
            }
        }
        var mode = request.Query["resolve"].ToString();
        var useSemantic =
            string.Equals(mode, "semantic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "1", StringComparison.OrdinalIgnoreCase);

        var graph = useSemantic
        ? SemanticGraph.ScanGraphSemantic(csPaths)
        : TypeScanner.ScanGraph(csPaths);
       // var types = TypeScanner.ScanTypes(csPaths);

        return Results.Ok(new
        {
            message = "uploaded",
            filecount = csPaths.Count,
            files = csPaths
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToArray(),
            summary = new
            {
                resolution = useSemantic ? "semantic" : "syntax",
                typecount = graph.Types.Count,
                edgecount = graph.Edges.Count,
            },
            types = graph.Types,
            edges = graph.Edges
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Upload failed: {ex.Message}");
    }
    finally
    {
        try
        {
            Directory.Delete(tempRoot, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Clean up failed: {ex.Message}");
        }
    }
});
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "http://localhost:5112/",
            UseShellExecute = true
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to open browser: {ex.Message}");
    }
});

app.Run();
