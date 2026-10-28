using Overseer.Cli;

return await OverseerCli.InvokeAsync(args, OverseerCli.ProcessEnvironment(), Console.Out, Console.Error);
