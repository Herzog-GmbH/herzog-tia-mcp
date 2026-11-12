using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TiaMcpServer.Cli;
using TiaMcpServer.Cli.Install;
using TiaMcpServer.Cursors;
using TiaMcpServer.Hmi;
using TiaMcpServer.Network;
using TiaMcpServer.OperationBatches;
using TiaMcpServer.ProjectTree;
using TiaMcpServer.Safety;
using TiaMcpServer.Safety.Pipeline;
using TiaMcpServer.Tools;
using TiaMcpServer.Worker;
using TiaMcpServer.Contracts.Project;
using TiaMcpServer.Contracts.Safety;

namespace TiaMcpServer
{
    internal static class Program
    {
        private const string ProduktivInstructions =
            "herzog-tia-mcp works on the project open in TIA Portal V21. Start with get_project_status, then bind_project. "
            + "Read blocks with plc_read; large ones arrive as a text file in contentFile, search and read that file instead of the response. "
            + "Change blocks and types with plc_write using edits: targeted replacements, nothing else in the object changes. "
            + "Nobody confirms a write while it runs: the server keeps a snapshot, compiles, compares the read-back and takes a change back when it causes new compile errors. "
            + "That proves the change compiles and arrived, not that the machine behaves; programmers review the changed code before it goes to a machine, so name every block you changed. "
            + "On rolled_back fix the change using blockImportOutcome.compileReport and send it again. Tell the user about protection.items[].importDifference. "
            + "plc_undo takes back the last calls of this server session and list shows them. Fail-safe blocks are refused. open_project, create_project, save_project_as, archive_project and close_project run without a question. Network and hardware changes and anything online or on a machine are not available: a person does that in TIA Portal. "
            + "Screens, PLC data types, instance DBs, watch tables, libraries and technology objects are in the second server (tia-hmi).";

        /// <summary>The Produktiv profile keeps snapshots and exports under one root in the user's profile.</summary>
        private static Plc.PlcProtection? CreateProtection(Contracts.Safety.McpAccessMode mode)
        {
            if (mode != Contracts.Safety.McpAccessMode.Produktiv)
            {
                return null;
            }

            var protection = new Plc.PlcProtection(Plc.PlcProtection.DefaultRoot());
            protection.Cleanup();
            Tools.PersonalDataFilter.Enable();
            Plc.PlcContentFiles.Configure(Path.Combine(protection.Root, "export"), autoThresholdChars: 30_000);
            return protection;
        }

        private static async Task<int> Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "install", StringComparison.OrdinalIgnoreCase))
            {
                return await InstallCommand.RunAsync(args[1..]);
            }

            if (args.Length > 0 && string.Equals(args[0], "doctor", StringComparison.OrdinalIgnoreCase))
            {
                return await DoctorCommand.RunAsync(args[1..]);
            }

            if (args.Length > 0 && (string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(args[0], "-v", StringComparison.OrdinalIgnoreCase)))
            {
                return VersionCommand.Run(Console.Out);
            }

            if (RemovedCliOptions.TryGetError(args, out var removedOptionError))
            {
                Console.Error.WriteLine($"Error: {removedOptionError}");
                return 1;
            }

            var accessModeResult = AccessModeParser.Parse(args);
            if (!accessModeResult.IsValid)
            {
                Console.Error.WriteLine($"Error: {accessModeResult.Error}");
                return 1;
            }

            var accessMode = accessModeResult.Mode;
            var accessPolicy = new OperationAccessPolicy(accessMode);
            if (!TryResolveStartupProjectPath(args, out var startupProjectPath, out var projectPathError))
            {
                Console.Error.WriteLine($"Error: {projectPathError}");
                return 1;
            }

            var budgetResult = PayloadBudgetParser.Parse(args);
            if (!budgetResult.IsValid)
            {
                Console.Error.WriteLine($"Error: {budgetResult.Error}");
                return 1;
            }

            StructuredOperationBatchPayloadBudget.Configure(budgetResult.ItemChars, budgetResult.DocumentChars);

            var workerTimeout = WorkerTimeoutParser.Parse(args);
            if (!workerTimeout.IsValid)
            {
                Console.Error.WriteLine($"Error: {workerTimeout.Error}");
                return 1;
            }

            Console.Error.WriteLine($"TIA MCP access mode: {McpAccessModeNames.ToName(accessMode).ToUpperInvariant()}");
            Console.Error.WriteLine($"TIA MCP response budget: {budgetResult.ItemChars} characters per value, {budgetResult.DocumentChars} per response");
            Console.Error.WriteLine($"TIA MCP worker timeout: {workerTimeout.Minutes} minutes");
            if (accessMode == McpAccessMode.ReadOnly)
            {
                Console.Error.WriteLine("Project opening, compilation, writes and lifecycle operations are disabled.");
            }

            // Application-specific access-mode switches have been parsed. Remove them
            // before generic-host configuration processes the remaining command line because
            // value-less switches such as --read-only are not valid configuration key/value pairs.
            var builder = Host.CreateApplicationBuilder(
                HostArgumentFilter.RemoveAccessModeArguments(args));
            builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Services.AddSingleton(new ProjectSessionBinding(startupProjectPath));
            builder.Services.AddSingleton(accessPolicy);
            builder.Services.AddSingleton(new Vci.VciBreadcrumbLocation());
            builder.Services.AddSingleton(sp => new OpennessWorkerClient(
                sp.GetRequiredService<ProjectSessionBinding>(),
                sp.GetRequiredService<ILogger<OpennessWorkerClient>>(),
                requestTimeout: workerTimeout.Timeout,
                accessPolicy: sp.GetRequiredService<OperationAccessPolicy>())
            {
                Protection = CreateProtection(accessMode),
            });
            builder.Services.AddSingleton(sp => new WriteExecution(
                new OpennessWriteBindingGate(sp.GetRequiredService<OpennessWorkerClient>()),
                new JsonlWriteAuditSink(), WriteGuardRegistration.ProductionCatalog(), TimeProvider.System));
            builder.Services.AddSingleton(_ => AuthenticatedCursorProtector.CreateProcessScoped());
            builder.Services.AddSingleton(sp => new HardwarePageCursorCodec(
                sp.GetRequiredService<AuthenticatedCursorProtector>()));
            builder.Services.AddSingleton(sp => new NetworkObjectPageCursorCodec(
                sp.GetRequiredService<AuthenticatedCursorProtector>()));
            builder.Services.AddSingleton(sp => new NetworkObjectPaginationCoordinator(
                sp.GetRequiredService<OpennessWorkerClient>(),
                sp.GetRequiredService<NetworkObjectPageCursorCodec>()));
            builder.Services.AddSingleton(sp => new HardwarePageProjector(
                sp.GetRequiredService<HardwarePageCursorCodec>()));
            builder.Services.AddSingleton(sp => new HardwarePaginationCoordinator(
                sp.GetRequiredService<OpennessWorkerClient>(),
                sp.GetRequiredService<HardwarePageCursorCodec>(),
                sp.GetRequiredService<HardwarePageProjector>()));
            builder.Services.AddSingleton(sp => new ProjectTreeCursorCodec(
                sp.GetRequiredService<AuthenticatedCursorProtector>()));
            builder.Services.AddSingleton(sp => new HmiPageCursorCodec(
                sp.GetRequiredService<AuthenticatedCursorProtector>()));
            builder.Services.AddSingleton(sp => new ProjectTreeSnapshotStore(TimeProvider.System));
            builder.Services.AddSingleton(sp => new ProjectTreePageProjector(
                sp.GetRequiredService<ProjectTreeCursorCodec>()));
            builder.Services.AddSingleton(sp => new ProjectTreeBrowseCoordinator(
                sp.GetRequiredService<OpennessWorkerClient>(),
                sp.GetRequiredService<ProjectTreeCursorCodec>(),
                sp.GetRequiredService<ProjectTreeSnapshotStore>(),
                sp.GetRequiredService<ProjectTreePageProjector>(),
                TimeProvider.System));
            builder.Services.AddSingleton(sp => new NetworkReadOperationExecutor(
                sp.GetRequiredService<OpennessWorkerClient>(),
                sp.GetRequiredService<HardwarePaginationCoordinator>(),
                sp.GetRequiredService<NetworkObjectPaginationCoordinator>()));

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                    {
                        Name = "herzog-tia-mcp",
                        Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
                    };
                    if (accessMode == Contracts.Safety.McpAccessMode.Produktiv)
                    {
                        options.ServerInstructions = ProduktivInstructions;
                    }
                })
                .WithStdioServerTransport()
                .WithAccessModeTools(accessMode);

            using var host = builder.Build();
            await host.RunAsync();
            return 0;
        }

        private static bool TryResolveStartupProjectPath(
            string[] args,
            out string? projectPath,
            out string? error)
        {
            projectPath = null;
            error = null;
            var source = "--project";
            for (int i = 0; i < args.Length; i++)
            {
                string? candidate = null;
                if (string.Equals(args[i], "--project", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length ||
                        string.IsNullOrWhiteSpace(args[i + 1]) ||
                        args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = "--project requires a value.";
                        return false;
                    }

                    candidate = args[++i];
                }
                else
                {
                    const string projectPrefix = "--project=";
                    if (args[i].StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        candidate = args[i].Substring(projectPrefix.Length);
                        if (string.IsNullOrWhiteSpace(candidate))
                        {
                            error = "--project requires a value.";
                            return false;
                        }
                    }
                }

                if (candidate is null)
                {
                    continue;
                }

                if (projectPath is not null)
                {
                    error = "--project may be specified only once.";
                    return false;
                }

                projectPath = candidate;
            }

            if (projectPath is null)
            {
                source = ConfiguredProjectPath.EnvVar;
                projectPath = Environment.GetEnvironmentVariable(ConfiguredProjectPath.EnvVar);
                if (string.IsNullOrWhiteSpace(projectPath))
                {
                    projectPath = null;
                    return true;
                }
            }

            var invalid = ConfiguredProjectPath.Validate(projectPath.Trim());
            if (invalid is not null)
            {
                error = $"{source} {invalid} Got '{projectPath}'.";
                return false;
            }

            return true;
        }
    }
}
