using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

#pragma warning disable ASPIREPROXYENDPOINTS001
IResourceBuilder<PostgresServerResource> postgres = builder
    .AddPostgres("postgres")
    .WithImage("postgres", "17.5")
    .WithDataVolume("dragalia-api-pgdata")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpointProxySupport(false);
#pragma warning restore ASPIREPROXYENDPOINTS001

#pragma warning disable ASPIRECERTIFICATES001
IResourceBuilder<RedisResource> redis = builder
    .AddRedis("redis")
    .WithImage("redis/redis-stack", "7.4.0-v8")
    .WithoutHttpsCertificate()
    .WithPassword(null)
    .WithEntrypoint("/entrypoint.sh") // Default Aspire entrypoint doesn't load modules correctly
    .WithLifetime(ContainerLifetime.Persistent);
#pragma warning restore ASPIRECERTIFICATES001

IResourceBuilder<ProjectResource> dragaliaApi = builder
    .AddProject<Projects.DragaliaAPI>("dragalia-api")
    .WithReference(postgres)
    .WithReference(redis)
    .WaitFor(postgres)
    .WaitFor(redis)
    // Fixed, unproxied port so the Caddy container can reach it; Caddy serves port 80.
    .WithEndpoint(
        "http",
        http =>
        {
            http.TargetHost = "0.0.0.0";
            http.Port = ApiPort;
            http.TargetPort = ApiPort;
            http.IsProxied = false;
        }
    )
    .WithExternalHttpEndpoints();

// Front proxy on port 80 for the game client and website, as in the wiki's
// "Advanced deployment" guide. See Caddyfile.
builder
    .AddContainer("caddy", "caddy", "2")
    .WithBindMount("Caddyfile", "/etc/caddy/Caddyfile", isReadOnly: true)
    .WithEnvironment("DAWNSHARD_API", "host.docker.internal:" + ApiPort)
    .WithEnvironment("DAWNSHARD_WEBSITE", "host.docker.internal:" + WebsitePort)
    .WithHttpEndpoint(port: 80, targetPort: 80, isProxied: false)
    .WithEndpoint("http", http => http.TargetHost = "0.0.0.0") // reachable from other devices
    .WithExternalHttpEndpoints();

if (builder.Configuration.GetValue<bool>("EnablePhoton"))
{
    string bearerToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    IResourceBuilder<ProjectResource> stateManager = builder
        .AddProject<Projects.DragaliaAPI_Photon_StateManager>("photon-state-manager")
        .WithReference(redis)
        .WithEndpoint("http", http => http.TargetHost = "0.0.0.0")
        .WithExternalHttpEndpoints()
        .WithEnvironment("PhotonOptions__Token", bearerToken);

    // This can't be configured with Aspire, but you also need to edit appsettings.Development.json to set
    // PhotonOptions:Url to the address your client should use to connect to the Photon server. For a typical setup
    // where you connect via your PC's local IP address, this should be 192.168.x.x:5055 (no http://)
    dragaliaApi = dragaliaApi
        .WithEnvironment("PhotonOptions__StateManagerUrl", stateManager.GetEndpoint("http"))
        .WithEnvironment("PhotonOptions__Token", bearerToken);

    // Corresponds to PhotonServer/ directory in repo root
    string photonServerDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "..",
        "PhotonServer",
        "deploy",
        "bin_Win64"
    );

    IResourceBuilder<ExecutableResource> photonServer = builder
        .AddExecutable(
            "photon-server",
            Path.Combine(photonServerDirectory, "PhotonSocketServer.exe"),
            photonServerDirectory,
            "/run",
            "LoadBalancing",
            "/configPath",
            photonServerDirectory
        )
        .WithEnvironment("ApiServerUrl", dragaliaApi.GetEndpoint("http"))
        .WithEnvironment("StateManagerUrl", stateManager.GetEndpoint("http"))
        .WithEnvironment("BearerToken", bearerToken)
        .WithEndpoint(
            "gs-to-ms", // https://doc.photonengine.com/server/current/operations/tcp-and-udp-port-numbers
            e =>
            {
                e.Port = 4520;
                e.IsProxied = false;
                e.IsExternal = true;
                e.Protocol = ProtocolType.Tcp;
            }
        )
        .WithEndpoint(
            "client-to-ms",
            e =>
            {
                e.Port = 5055;
                e.IsProxied = false;
                e.IsExternal = true;
                e.Protocol = ProtocolType.Udp;
            }
        )
        .WithEndpoint(
            "client-to-gs",
            e =>
            {
                e.Port = 5056;
                e.IsProxied = false;
                e.IsExternal = true;
                e.Protocol = ProtocolType.Udp;
            }
        );
}

if (builder.Configuration.GetValue<bool>("EnableWebsite"))
{
    builder
        .AddNpmApp("website", workingDirectory: "../../Website", scriptName: "dev")
        .WithEnvironment("PUBLIC_ENABLE_MSW", "false")
        .WithEnvironment("DAWNSHARD_API_URL_SSR", dragaliaApi.GetEndpoint("http"))
        // Requests through Caddy keep the Host header, e.g. <this machine>.local
        .WithEnvironment("__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS", ".local")
        .WithHttpEndpoint(null, WebsitePort, "http");
}

builder.Build().Run();

internal static partial class Program
{
    private const int ApiPort = 5080;
    private const int WebsitePort = 3001;
}
