using System.Diagnostics;
using System.Globalization;
using SakritCraft.Render;
using SakritCraft.Render.Cameras;
using SakritCraft.Render.Terrain;
using SakritCraft.World.Seed;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

// SakritCraft client entry point. M2: open a window, generate a region of terrain on worker threads,
// stream it to the GPU and fly through it. Command line (all optional):
//   --exit-after <seconds>   close automatically (smoke tests / CI)
//   --gpu <index>            force a physical device by enumeration index
//   --fps <n>                CPU frame limiter target (0 = uncapped); default = monitor refresh rate
//   --fifo                   use FIFO (vsync) presentation instead of mailbox
//   --no-hot-reload          disable the shader file watcher
//   --trace                  verbose renderer logging (includes validation info messages)
//   --seed <text>            world seed (integer or any text; default "sakrit")
//   --region <n>             start-up region size in chunks along X and Z (default 20)
//   --centre x,z             world position the region is generated around, metres (default 0,0)
//   --wireframe              start in the wireframe debug view (F1 toggles)
//   --camera x,y,z,yaw,pitch override the start camera (metres, degrees) for reproducible screenshots
// Controls: click to capture the mouse, Esc releases. WASD move, mouse look, Shift sprint,
//           Space/Ctrl up/down, scroll wheel changes speed, F1 toggles wireframe.

string repoRoot = FindRepoRoot();
double? exitAfter = null;
int? gpuIndex = null;
double? targetFps = null;
var present = PresentPreference.Mailbox;
bool hotReload = true;
string seedText = "sakrit";
int regionChunks = 20;
bool startWireframe = false;
double centreX = 0.0, centreZ = 0.0;
double startTime = 0.515;
string? screenshotPath = null;
double screenshotAt = 5.0;
bool screenshotTaken = false;
double[]? cameraOverride = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--exit-after" when i + 1 < args.Length:
            exitAfter = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--gpu" when i + 1 < args.Length:
            gpuIndex = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--fps" when i + 1 < args.Length:
            targetFps = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--fifo":
            present = PresentPreference.Fifo;
            break;
        case "--no-hot-reload":
            hotReload = false;
            break;
        case "--trace":
            RenderLog.MinLevel = RenderLogLevel.Trace;
            break;
        case "--seed" when i + 1 < args.Length:
            seedText = args[++i];
            break;
        case "--region" when i + 1 < args.Length:
            regionChunks = Math.Clamp(int.Parse(args[++i], CultureInfo.InvariantCulture), 1, 64);
            break;
        case "--wireframe":
            startWireframe = true;
            break;
        case "--centre" when i + 1 < args.Length:
        {
            var parts = args[++i].Split(',');
            if (parts.Length != 2)
            {
                Console.Error.WriteLine("--centre expects x,z");
                return 2;
            }

            centreX = double.Parse(parts[0], CultureInfo.InvariantCulture);
            centreZ = double.Parse(parts[1], CultureInfo.InvariantCulture);
            break;
        }
        case "--time" when i + 1 < args.Length:
            startTime = Math.Clamp(double.Parse(args[++i], CultureInfo.InvariantCulture), 0.0, 0.999);
            break;
        case "--screenshot" when i + 1 < args.Length:
            screenshotPath = args[++i];
            break;
        case "--screenshot-at" when i + 1 < args.Length:
            screenshotAt = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--camera" when i + 1 < args.Length:
            cameraOverride = args[++i].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            if (cameraOverride.Length != 5)
            {
                Console.Error.WriteLine("--camera expects x,y,z,yaw,pitch");
                return 2;
            }
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'");
            return 2;
    }
}

var seed = WorldSeed.FromText(seedText);
RenderLog.Info("client", $"World seed \"{seedText}\" -> {seed}");

var windowOptions = WindowOptions.DefaultVulkan with
{
    Title = "SakritCraft",
    Size = new Vector2D<int>(1600, 900),
    IsEventDriven = false,
    FramesPerSecond = 0,     // the renderer paces itself
    UpdatesPerSecond = 0,
    VSync = false,           // present mode decides, not the windowing layer
};

var window = Window.Create(windowOptions);
Renderer? renderer = null;
FlyCameraController? controller = null;
SakritCraft.Client.GameSession? session = null;
int exitCode = 0;
var runClock = Stopwatch.StartNew();
long lastFrameTicks = 0;

window.Load += () =>
{
    try
    {
        double refresh = window.Monitor?.VideoMode.RefreshRate ?? 60;
        renderer = new Renderer(window, new RendererOptions
        {
            ShaderDirectory = Path.Combine(repoRoot, "shaders"),
            PipelineCacheFile = Path.Combine(repoRoot, "artifacts", "pipeline.cache"),
            PresentMode = present,
            TargetFrameRate = targetFps ?? refresh,
            ForcePhysicalDeviceIndex = gpuIndex,
            EnableShaderHotReload = hotReload,
        }, new TerrainOptions
        {
            Seed = seed,
            CentreX = centreX,
            CentreZ = centreZ,
            RegionChunksX = regionChunks,
            RegionChunksZ = regionChunks,
        });

        if (cameraOverride is not null)
        {
            renderer.Camera.Position = new Vector3D<double>(cameraOverride[0], cameraOverride[1], cameraOverride[2]);
            renderer.Camera.Yaw = (float)(cameraOverride[3] * Math.PI / 180.0);
            renderer.Camera.Pitch = (float)(cameraOverride[4] * Math.PI / 180.0);
        }

        session = new SakritCraft.Client.GameSession(
            renderer.TerrainField, seed, renderer.Camera.Position, startTime);

        controller = new FlyCameraController(renderer.Camera, window.CreateInput(), startWireframe);
        lastFrameTicks = runClock.ElapsedTicks;
        RenderLog.Info("client", "Click the window to capture the mouse; Esc releases. WASD / Shift / Space / Ctrl to fly, F1 wireframe.");
    }
    catch (Exception ex)
    {
        RenderLog.Error("client", $"Renderer start-up failed: {ex}");
        exitCode = 1;
        window.Close();
    }
};

window.FramebufferResize += size => renderer?.NotifyFramebufferResize(size.X, size.Y);

window.Render += _ =>
{
    if (renderer is null)
    {
        return;
    }

    try
    {
        long now = runClock.ElapsedTicks;
        double dt = (now - lastFrameTicks) / (double)Stopwatch.Frequency;
        lastFrameTicks = now;

        if (controller is not null)
        {
            controller.Update(dt);
            renderer.Wireframe = controller.WireframeRequested;
        }

        if (session is not null)
        {
            session.Update(dt, renderer.Camera.Position);
            session.ApplyLighting(renderer.Lighting);
            session.Populate(renderer.EntityBatch, renderer.Camera);
            renderer.StatusLine = session.Status();
        }

        renderer.RenderFrame();
    }
    catch (Exception ex)
    {
        RenderLog.Error("client", $"Frame failed: {ex}");
        exitCode = 1;
        window.Close();
        return;
    }

    if (screenshotPath is not null && !screenshotTaken && runClock.Elapsed.TotalSeconds >= screenshotAt)
    {
        renderer.RequestScreenshot(screenshotPath);
        screenshotTaken = true;
    }

    if (exitAfter is double limit && runClock.Elapsed.TotalSeconds >= limit)
    {
        RenderLog.Info("client", $"--exit-after {limit}s reached; closing.");
        window.Close();
    }
};

window.Closing += () =>
{
    controller?.Dispose();
    controller = null;
    renderer?.Dispose();
    renderer = null;
};

window.Run();
window.Dispose();
return exitCode;

// Locate the repo root (the directory containing SakritCraft.sln) from the executable location, so
// shaders resolve whether the app is started from the IDE, `dotnet run`, or the bin folder.
static string FindRepoRoot()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SakritCraft.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }
    }

    return Directory.GetCurrentDirectory();
}
