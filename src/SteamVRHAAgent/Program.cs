using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using SteamVRHAAgent;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
var configPath = Paths.DefaultConfigFile;
int? portOverride = null;
var verbose = false;
var printConfig = false;

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
        case "--launched-by-steamvr":
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

// Only one agent per user: SteamVR may try to launch us while the systemd service is already running.
FileStream instanceLock;
try
{
    Directory.CreateDirectory(Paths.RuntimeDir);
    instanceLock = new FileStream(Paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (IOException)
{
    Log.Info("Another instance of the agent is already running; exiting.");
    return 0;
}

using var instanceLockHandle = instanceLock;

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
