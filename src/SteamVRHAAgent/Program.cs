using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using SteamVRHAAgent;
using SteamVRHAAgent.UI;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

var version = Paths.Version;
var configPath = Paths.DefaultConfigFile;
int? portOverride = null;
var verbose = false;
var printConfig = false;
var headless = false;
var launchedBySteamVR = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h" or "--help":
            Console.WriteLine($"""
                Home Assistant Agent for SteamVR (Linux) {version}

                Usage: {Paths.AppId} [options]

                  -c, --config <path>     Config file (default: {Paths.DefaultConfigFile})
                  -p, --port <port>       Override the WebSocket port from the config
                  -v, --verbose           Verbose logging
                      --headless          No window or tray icon (automatic without a display)
                      --print-config      Print the effective config and exit
                      --version           Print the version and exit
                  -h, --help              Show this help
                """);
            return 0;
        case "--version":
            Console.WriteLine(version);
            return 0;
        case "-c" or "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "-p" or "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
            portOverride = port;
            i++;
            break;
        case "-v" or "--verbose":
            verbose = true;
            break;
        case "--print-config":
            printConfig = true;
            break;
        case "--headless":
            headless = true;
            break;
        case "--launched-by-steamvr":
            launchedBySteamVR = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]} (see --help)");
            return 2;
    }
}

AgentConfig config;
try
{
    config = AgentConfig.Load(configPath);
}
catch (Exception e)
{
    Log.Error($"Could not read config {configPath}: {e.Message}");
    return 1;
}

if (portOverride is { } p) config.Port = p;
if (config.Runtime is not ("auto" or "steamvr" or "monado"))
{
    Log.Error($"Invalid runtime '{config.Runtime}' in config; use \"auto\", \"steamvr\" or \"monado\"");
    return 1;
}
Log.Verbose = verbose || config.VerboseLogging;

if (printConfig)
{
    Console.WriteLine(config);
    return 0;
}

// Only one agent per user. A second launch (app menu, SteamVR, terminal) activates the running one instead;
// like the Windows app, a launch by SteamVR doesn't pop up the window.
using var instance = SingleInstance.TryAcquire(
    launchedBySteamVR ? SingleInstance.ActivationSteamVR : SingleInstance.ActivationShow);
if (instance == null)
{
    Log.Info("Another instance of the agent is already running; activated it and exiting.");
    return 0;
}

Log.Info($"Home Assistant Agent for SteamVR {version} starting (config: {configPath})");
NativeLibraries.Install(config.OpenVRLibraryPath);

using var agent = new Agent(config);
try
{
    agent.Start();
}
catch (HttpListenerException e)
{
    Log.Error($"Could not listen on port {config.Port}: {e.Message}");
    return 1;
}

var hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                 !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
if (!headless && hasDisplay)
{
    App.Agent = agent;
    App.ConfigPath = configPath;
    instance.Activated += activation =>
    {
        if (activation == SingleInstance.ActivationShow) App.Current.ShowMainWindow();
    };

    using var guiSigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        App.Current.Exit();
    });
    using var guiSigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
    {
        ctx.Cancel = true;
        App.Current.Exit();
    });
    _ = agent.Exited.ContinueWith(_ => App.Current.Exit());

    // The agent's lifetime is handled by systemd (or the user), not the desktop's X11 session manager;
    // Avalonia reads this variable directly and otherwise logs SMLib/ICELib errors.
    if (Environment.GetEnvironmentVariable("AVALONIA_X11_USE_SESSION_MANAGEMENT") == null)
        Environment.SetEnvironmentVariable("AVALONIA_X11_USE_SESSION_MANAGEMENT", "0");

    try
    {
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);

        // Avalonia leaves its SynchronizationContext on this thread, but its dispatcher no longer runs, so
        // awaiting here would post continuations nowhere and hang. Shut down on the thread pool instead.
        SynchronizationContext.SetSynchronizationContext(null);
        Log.Info("Shutting down");
        await Task.Run(agent.StopAsync);
        return 0;
    }
    catch (Exception e)
    {
        // No usable display after all: keep serving Home Assistant without the window.
        Log.Error($"Could not start the user interface, continuing without it: {e.Message}");
    }
}

var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    stop.TrySetResult();
});
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
{
    ctx.Cancel = true;
    stop.TrySetResult();
});

await Task.WhenAny(stop.Task, agent.Exited);
Log.Info("Shutting down");
await agent.StopAsync();
return 0;
